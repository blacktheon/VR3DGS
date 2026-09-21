"""Round-one scoring and independent candidate verification."""

import hashlib
import html
import json
import math
import os
from pathlib import Path
import struct
import subprocess
import time
import zlib

import numpy as np

from .contracts import SourceManifest, file_sha256, write_json_atomic
from .ranking import keep_count
from .scene_geometry import world_gaussians, floor_eligible, generate_views, scene_hash

def freeze_eligible_rank(scores, eligible):
    scores=np.asarray(scores,dtype=np.float64);eligible=np.asarray(eligible,dtype=bool)
    if scores.ndim!=1 or scores.shape!=eligible.shape or not np.isfinite(scores).all():
        raise ValueError("Scores must be finite and match the eligibility array")
    ids=np.flatnonzero(eligible).astype("<u4")
    return ids[np.lexsort((ids,-scores[ids]))]

def image_metrics(reference_rgb, reference_alpha, candidate_rgb, candidate_alpha):
    delta=np.asarray(candidate_rgb,dtype=np.float64)-reference_rgb
    alpha=np.abs(np.asarray(candidate_alpha,dtype=np.float64)-reference_alpha)
    mse=float(np.mean(delta*delta))
    return {"rgb_mae":float(np.mean(np.abs(delta))),"rgb_psnr":None if mse==0 else -10*math.log10(mse),
            "alpha_mae":float(np.mean(alpha)),"alpha_max":float(np.max(alpha)),
            "silhouette_mismatch":float(np.mean((reference_alpha>=.5)!=(candidate_alpha>=.5)))}

def validate_membership(views):
    seen=set()
    for label in ("scoring","verify","regression"):
        for view in views[label]:
            key=tuple(np.round(np.r_[view["viewmat"],view["K"],view["width"],view["height"]],6))
            if key in seen: raise ValueError("Scoring/verification camera membership overlaps")
            seen.add(key)


def _png(path, image):
    pixels=(np.clip(image,0,1)*255+.5).astype(np.uint8)
    if pixels.ndim==2: pixels=np.repeat(pixels[:,:,None],3,axis=2)
    if pixels.shape[2]==1: pixels=np.repeat(pixels,3,axis=2)
    h,w,_=pixels.shape
    def chunk(kind,data):
        return struct.pack(">I",len(data))+kind+data+struct.pack(">I",zlib.crc32(kind+data)&0xffffffff)
    raw=b"".join(b"\0"+row.tobytes() for row in pixels)
    Path(path).write_bytes(b"\x89PNG\r\n\x1a\n"+chunk(b"IHDR",struct.pack(">IIBBBBB",w,h,8,2,0,0,0))+
                          chunk(b"IDAT",zlib.compress(raw,6))+chunk(b"IEND",b""))


class GaussianScene:
    """Resident full eligible source; each camera retains its full occlusion context."""
    def __init__(self, arrays, original_ids, scene, rear_depth):
        import torch
        self.torch=torch;self.scene=scene;self.rear_depth=rear_depth
        self.ids=torch.as_tensor(original_ids.astype(np.int64),device="cuda")
        self.data={k:torch.as_tensor(np.ascontiguousarray(v),device="cuda") for k,v in arrays.items()}
        self.wall_candidates=[]
        means=arrays["means"]
        for wall in scene["walls"]:
            if wall["floor"]: continue
            distance=(means-np.asarray(wall["position"])) @ np.asarray(wall["normal"])
            subset=np.flatnonzero((distance>=-rear_depth)&(distance<0))
            self.wall_candidates.append((wall,torch.as_tensor(subset,device="cuda"),
                                         torch.as_tensor(distance[subset],device="cuda",dtype=torch.float32)))

    def opacities(self, eye):
        torch=self.torch;opacity=self.data["opacities"].clone()
        eye_np=np.asarray(eye)
        eye_t=torch.tensor(eye,device="cuda",dtype=torch.float32)
        for wall,indices,distance in self.wall_candidates:
            side=float((eye_np-wall["position"]) @ np.asarray(wall["normal"]))
            if side<=1e-6 or len(indices)==0: continue
            hit=eye_t+(self.data["means"][indices]-eye_t)*(side/(side-distance))[:,None]
            inverse=torch.tensor(wall["world_to_local"],device="cuda",dtype=torch.float32).reshape(4,4)
            local=hit @ inverse[:3,:3].T+inverse[:3,3]
            lo,hi=wall["bounds_min"],wall["bounds_max"]
            blocked=(local[:,0]>=lo[0])&(local[:,0]<=hi[0])&(local[:,2]>=lo[2])&(local[:,2]<=hi[2])
            opacity[indices[blocked]]=0
        return opacity

    def render(self, view, selected=None):
        torch=self.torch
        from gsplat import rasterization
        if selected is not None and len(selected)==0:
            # gsplat 1.5.3's native projection divides by N; never dispatch N=0.
            rgb=torch.zeros((1,view["height"],view["width"],3),device="cuda")
            alpha=torch.zeros((1,view["height"],view["width"],1),device="cuda")
            return rgb,alpha,{},{}
        data=self.data if selected is None else {k:v[selected] for k,v in self.data.items()}
        opacity=self.opacities(view["eye"])
        if selected is not None: opacity=opacity[selected]
        rgb,alpha,meta=rasterization(means=data["means"],quats=None,scales=None,covars=data["covars"],
            opacities=opacity,colors=data["colors"],
            viewmats=torch.tensor(view["viewmat"],device="cuda",dtype=torch.float32).reshape(1,4,4),
            Ks=torch.tensor(view["K"],device="cuda",dtype=torch.float32).reshape(1,3,3),
            width=view["width"],height=view["height"],near_plane=view["near"],far_plane=view["far"],
            packed=True,radius_clip=0,eps2d=.3,tile_size=16,rasterize_mode="classic")
        return rgb,alpha,meta,data


def _prepare_source(manifest, scene, context):
    from .source import read_source
    n=manifest.vertex_count
    arrays={"means":np.empty((n,3),dtype=np.float32),"covars":np.empty((n,3,3),dtype=np.float32),
            "opacities":np.empty(n,dtype=np.float32),"colors":np.empty((n,3),dtype=np.float32)}
    with read_source(manifest) as source:
        if manifest.sh_degree!=0: raise ValueError("This authored first-round profile requires SH0 source")
        for start in range(0,n,65536):
            context.check_cancelled();stop=min(n,start+65536)
            transformed=world_gaussians(source.decoded_slice(start,stop),scene)
            for name in arrays: arrays[name][start:stop]=transformed[name]
    eligible=floor_eligible(arrays["means"],scene)
    ids=np.flatnonzero(eligible).astype("<u4")
    if not len(ids): raise ValueError("Floor exclusion leaves no eligible splats")
    return {name:value[ids] for name,value in arrays.items()},ids,eligible


def _write_html(path, report):
    rows=[]
    for percent,metrics in report["summary"].items():
        rows.append(f"<tr><td>{percent}%</td><td>{metrics['kept_count']:,}</td><td>{metrics['mean_rgb_mae']:.6f}</td><td>{metrics['max_rgb_mae']:.6f}</td><td>{metrics['max_alpha_mae']:.6f}</td></tr>")
    views=[]
    ordered=sorted(report["views"],key=lambda v:v["candidates"]["25"]["rgb_mae"],reverse=True)
    for view in ordered:
        figures=[]
        for percent in (100,75,50,25):
            name=f"{view['id']}-{percent}.png"
            figures.append(f"<figure><img loading='lazy' src='images/{name}'><figcaption>{percent}%</figcaption></figure>")
        views.append(f"<section><h2>{html.escape(view['id'])} · {html.escape(view['membership'])}</h2><div class='images'>{''.join(figures)}</div><details><summary>25% difference ×4 and alpha</summary><img width='320' src='images/{view['id']}-25-diff.png'><img width='320' src='images/{view['id']}-25-alpha.png'></details></section>")
    path.write_text("<!doctype html><meta charset='utf-8'><title>Stage 1 · Round 1</title><style>body{font:16px system-ui;background:#131923;color:#e7edf7;margin:32px;max-width:1600px}table{border-collapse:collapse}td,th{padding:10px 18px;border-bottom:1px solid #40516a}.images{display:grid;grid-template-columns:repeat(4,1fr);gap:12px}figure{margin:0}img{width:100%}details img{max-width:320px}section{margin-top:32px}code{overflow-wrap:anywhere}</style>"+
        f"<h1>Round 1 · Authored NavMesh and directional walls</h1><p>Original {report['source_count']:,}; eligible baseline {report['eligible_count']:,}; below-floor exclusions {report['floor_excluded_count']:,}. Percentages use the eligible baseline.</p>"+
        f"<p>Scoring {report['view_counts']['scoring']} · held-out {report['view_counts']['verify']} · fixed regression {report['view_counts']['regression']}. Finite sampled coverage; no Android performance or invisible-everywhere claim. No panel/label ROIs were authored, so small-control acceptability still needs your inspection.</p>"+
        f"<p>Rank <code>{report['rank_id']}</code>. Python candidate versus Python eligible original; black background. These metrics exclude Unity/packing differences.</p>"+
        "<table><tr><th>Keep</th><th>Splats</th><th>Mean RGB MAE</th><th>Worst RGB MAE</th><th>Worst alpha MAE</th></tr>"+"".join(rows)+"</table>"+"".join(views),encoding="utf-8")

def run_round1(context, request):
    from .environment import configure_toolchain
    from .backends.gsplat_backend import load_cuda_backend
    from .backends.contributions import load_accumulator,accumulate_projected
    import torch
    started=time.monotonic();root=context.output_root;result=context.result_dir
    scene_path=Path(request.get("scene_path") or root/"round1-input/scene.json")
    scene=json.loads(scene_path.read_text(encoding="utf-8-sig"))
    pointer=json.loads((root/"current_inspect.json").read_text(encoding="utf-8-sig"))
    manifest=SourceManifest.load(Path(pointer["result_dir"])/"source_manifest.json")
    if Path(manifest.source_path).resolve()!=Path(request["source_path"]).resolve():
        raise ValueError("Inspected source path differs from this request")
    if scene.get("source_hash",manifest.sha256)!=manifest.sha256: raise ValueError("Scene/source hash mismatch")
    profile={"rear_depth":.1,"height_reference":"above_each_navmesh_surface","floor_rule":"center_signed_distance>=0",
             "wall_rule":"positive_camera_side_finite_ray_intersection_center_rear_band","color":"SH0 RGB without Unity gamma workaround",
             "eps2d":.3,"radius_clip":0,"rasterize_mode":"classic","background":[0,0,0],"quantization_fraction_bits":24}
    profile.update(request.get("profile",{}))
    if not 0<profile["rear_depth"]<=.1: raise ValueError("Wall rear depth must be within (0,.1]")
    fingerprint=scene_hash(scene)
    sampling=json.loads(request["sampling_json"]) if request.get("sampling_json") else request.get("sampling",{})
    views=generate_views(scene,**sampling);validate_membership(views)
    write_json_atomic(result/"scene.json",scene)
    for name,values in views.items():
        (result/f"views_{name}.jsonl").write_text("".join(json.dumps(v,allow_nan=False)+"\n" for v in values),encoding="utf-8")
    context.progress(.02,"Decoding full original and applying the authored floor baseline")
    arrays,ids,eligible=_prepare_source(manifest,scene,context);n=manifest.vertex_count;m=len(ids)
    ids.tofile(result/"eligible_ids.bin");np.flatnonzero(~eligible).astype("<u4").tofile(result/"floor_excluded_ids.bin")
    cache=Path(request.get("backend_cache_root") or root)
    toolchain=configure_toolchain(cache)
    capability=torch.cuda.get_device_capability();os.environ["TORCH_CUDA_ARCH_LIST"]=f"{capability[0]}.{capability[1]}"
    context.progress(.06,"Loading verified CUDA projection and contribution kernels")
    stock_backend=load_cuda_backend(cache);extension=load_accumulator(cache)
    torch.cuda.reset_peak_memory_stats()
    with torch.no_grad():
        gaussian=GaussianScene(arrays,ids,scene,profile["rear_depth"]);del arrays
        peak=np.zeros(n,dtype=np.float32);normalized_sum=np.zeros(n,dtype=np.float64)
        coverage=np.zeros(n,dtype=np.uint32);pixels=np.zeros(n,dtype=np.uint64);projected=np.zeros(n,dtype=np.uint32)
        maximum_renderer_error=0.;scoring_times=[]
        for i,view in enumerate(sorted(views["scoring"],key=lambda v:v["id"])):
            context.progress(.08+.52*i/max(1,len(views["scoring"])),f"Scoring {i+1}/{len(views['scoring'])} · {m:,} eligible splats")
            tick=time.monotonic();rgb,alpha,meta,data=gaussian.render(view)
            packed=meta["gaussian_ids"].long();source_ids=gaussian.ids[packed]
            stats=accumulate_projected(extension,meta,data["colors"][packed],source_ids,n)
            error=max(float((stats["rgb"]-rgb).abs().max()),float((stats["alpha"]-alpha).abs().max()))
            maximum_renderer_error=max(maximum_renderer_error,error)
            if error>1e-5: raise RuntimeError(f"Instrumented and stock rendering disagree: {error}")
            p=stats["peak"].cpu().numpy();s=stats["sum_fixed"].cpu().numpy();h=stats["hits"].cpu().numpy()
            np.maximum(peak,p,out=peak);normalized_sum+=s.astype(np.float64)/(2**24*view["width"]*view["height"])
            coverage+=(p>=1/255).astype(np.uint32);pixels+=h.astype(np.uint64)
            projected[source_ids.cpu().numpy()]+=1
            scoring_times.append(time.monotonic()-tick)
            del rgb,alpha,meta,data,stats,p,s,h,packed,source_ids
        count=len(views["scoring"])
        if not count: raise ValueError("No scoring cameras")
        score=.70*peak.astype(np.float64)+.20*normalized_sum/count+.10*coverage/count
        order=freeze_eligible_rank(score,eligible)
        order.tofile(result/"rank.bin");score.astype("<f4").tofile(result/"importance.bin")
        np.savez_compressed(result/"contribution_statistics.npz",peak=peak,normalized_sum=normalized_sum,
            coverage_views=coverage,contributing_pixels=pixels,projected_views=projected,unobserved=(pixels==0))
        rank_hash=file_sha256(result/"rank.bin")
        rank_id=f"round1-{manifest.sha256[:8]}-{fingerprint[:8]}-{rank_hash[:12]}"
        rank_manifest={"schema_version":1,"rank_id":rank_id,"source_hash":manifest.sha256,"scene_hash":fingerprint,
            "source_count":n,"eligible_count":m,"floor_excluded_count":n-m,"rank_path":"rank.bin",
            "rank_sha256":rank_hash,"importance_path":"importance.bin","profile":profile,
            "scoring_views":count,"score_formula":"0.70*peak + 0.20*mean_normalized_sum + 0.10*coverage_fraction; surface_bonus=0",
            "ordering":"descending_f64_score_then_ascending_original_source_id","eligible_unobserved_count":int(np.sum((pixels==0)&eligible)),
            "quantization_error_bound":"per-source summed contributing_pixels / 2^24",
            "coordinate_profile":"RUB_to_RUF_once","calibration_status":scene.get("calibration_status","user_authored_unity_units")}
        write_json_atomic(result/"rank_manifest.json",rank_manifest)
        context.progress(.62,"Frozen rank complete; verifying independent viewpoints")
        inverse=np.full(n,-1,dtype=np.int64);inverse[ids]=np.arange(m)
        subsets={p:torch.tensor(inverse[np.sort(order[:keep_count(m,p*100)])],device="cuda") for p in (75,50,25)}
        image_dir=result/"images";image_dir.mkdir()
        reports=[];verification_views=views["verify"]+views["regression"]
        for index,view in enumerate(verification_views):
            context.progress(.63+.32*index/max(1,len(verification_views)),f"Verifying {index+1}/{len(verification_views)} · 100/75/50/25/0%")
            rgb,alpha,_,_=gaussian.render(view)
            reference=rgb[0].cpu().numpy();reference_alpha=alpha[0].cpu().numpy();del rgb,alpha
            _png(image_dir/f"{view['id']}-100.png",reference)
            entry={"id":view["id"],"membership":view["membership"],"candidates":{}}
            for percent in (100,75,50,25,0):
                context.check_cancelled()
                if percent==100: candidate,candidate_alpha=reference,reference_alpha
                elif percent==0: candidate,candidate_alpha=np.zeros_like(reference),np.zeros_like(reference_alpha)
                else:
                    rgb,alpha,_,_=gaussian.render(view,subsets[percent]);candidate=rgb[0].cpu().numpy();candidate_alpha=alpha[0].cpu().numpy();del rgb,alpha
                metrics=image_metrics(reference,reference_alpha,candidate,candidate_alpha)
                entry["candidates"][str(percent)]={**metrics,"kept_count":keep_count(m,percent*100)}
                if percent not in (0,100):
                    _png(image_dir/f"{view['id']}-{percent}.png",candidate)
                    _png(image_dir/f"{view['id']}-{percent}-diff.png",np.abs(candidate-reference)*4)
                    _png(image_dir/f"{view['id']}-{percent}-alpha.png",np.abs(candidate_alpha-reference_alpha)*4)
            reports.append(entry)
        summary={}
        for percent in (100,75,50,25,0):
            values=[v["candidates"][str(percent)] for v in reports]
            if not values: raise ValueError("No independent verification cameras")
            summary[str(percent)]={"kept_count":keep_count(m,percent*100),
                "mean_rgb_mae":float(np.mean([v["rgb_mae"] for v in values])),
                "max_rgb_mae":max(v["rgb_mae"] for v in values),"max_alpha_mae":max(v["alpha_mae"] for v in values)}
        driver=subprocess.run(["nvidia-smi","--query-gpu=driver_version","--format=csv,noheader"],capture_output=True,text=True,timeout=10,creationflags=subprocess.CREATE_NO_WINDOW)
        backend={"stock":{**stock_backend,"extension_sha256":file_sha256(Path(stock_backend["extension_path"]))},
                 "accumulator_binary":extension.__file__,"accumulator_sha256":file_sha256(Path(extension.__file__)),
                 "cuda_source_sha256":file_sha256(Path(__file__).parent/"backends/cuda/forward.cu"),"toolchain":toolchain,
                 "torch":torch.__version__,"device":torch.cuda.get_device_name(),"driver":driver.stdout.strip() if driver.returncode==0 else "unavailable",
                 "stock_instrumented_max_error":maximum_renderer_error,"peak_allocated_bytes":torch.cuda.max_memory_allocated(),
                 "peak_reserved_bytes":torch.cuda.max_memory_reserved()}
        report={"schema_version":1,"rank_id":rank_id,"source_hash":manifest.sha256,"scene_hash":fingerprint,
            "source_count":n,"eligible_count":m,"floor_excluded_count":n-m,"view_counts":{k:len(v) for k,v in views.items()},
            "profile":profile,"summary":summary,"views":reports,"backend":backend,
            "scoring_seconds":float(sum(scoring_times)),"total_seconds":time.monotonic()-started,
            "panel_regions":"No explicit panel/label ROIs authored; targeted views do not establish label preservation",
            "android_performance":"unmeasured","scene_scale":"user_authored_unity_units"}
        write_json_atomic(result/"report.json",report);_write_html(result/"report.html",report)
    context.progress(1,"Round-one rank and independent verification report complete")
    return {"rank_manifest":"rank_manifest.json","report":"report.json","report_html":"report.html"}
