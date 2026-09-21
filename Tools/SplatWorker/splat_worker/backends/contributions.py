"""Actual front-to-back contribution statistics; no visibility proxy."""

from pathlib import Path
from ..contracts import write_json_atomic
from .gsplat_backend import _build_directory

def composite_weights(alphas):
    weights=[0.]*len(alphas); T=1.
    for i,raw in enumerate(alphas):
        alpha=min(.999,float(raw))
        if alpha<1/255: continue
        next_T=T*(1-alpha)
        if next_T<=1e-4: break
        weights[i]=T*alpha;T=next_T
    return weights

def load_accumulator(output_root):
    from torch.utils.cpp_extension import load
    directory=Path(__file__).parent/"cuda"
    build,pointer=_build_directory(output_root,"stage1_contributions")
    extension=load(name="stage1_contributions",sources=[str(directory/"bindings.cpp"),str(directory/"forward.cu")],
                   extra_cflags=["/O2"],extra_cuda_cflags=["-O3","-use_fast_math"],
                   build_directory=str(build),verbose=False)
    write_json_atomic(pointer,{"directory":build.name})
    return extension

def accumulate_projected(extension, meta, packed_colors, source_ids, source_count):
    if meta["means2d"].ndim!=2 or meta["isect_offsets"].shape[0]!=1:
        raise ValueError("Contribution profile requires packed projection and one camera")
    width,height=int(meta["width"]),int(meta["height"])
    if width*height>=2**32:
        raise ValueError("Image exceeds the fixed-point sum overflow guard")
    result=extension.forward(meta["means2d"].contiguous(),meta["conics"].contiguous(),
        packed_colors.contiguous(),meta["opacities"].contiguous(),meta["isect_offsets"].contiguous(),
        meta["flatten_ids"].contiguous(),source_ids.long().contiguous(),int(source_count),width,height,int(meta["tile_size"]))
    return dict(zip(("rgb","alpha","peak","sum_fixed","hits"),result))
