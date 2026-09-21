import numpy as np
import pytest
import json

from splat_worker.round1 import freeze_eligible_rank, image_metrics, validate_membership


def test_floor_baseline_and_rank_ties_preserve_original_source_ids():
    score=np.array([100,.5,.8,.8,0.],dtype=np.float64)
    eligible=np.array([False,True,True,True,True])
    assert freeze_eligible_rank(score,eligible).tolist()==[2,3,1,4]
    score[2]=np.nan
    with pytest.raises(ValueError,match="finite"):
        freeze_eligible_rank(score,eligible)


def test_verification_reports_actual_rgb_alpha_and_silhouette_damage():
    ref=np.ones((2,2,3)); alpha=np.ones((2,2,1))
    same=image_metrics(ref,alpha,ref,alpha)
    assert same["rgb_mae"]==0 and same["alpha_mae"]==0 and same["silhouette_mismatch"]==0
    zero=image_metrics(ref,alpha,np.zeros_like(ref),np.zeros_like(alpha))
    assert zero["rgb_mae"]==1 and zero["alpha_max"]==1 and zero["silhouette_mismatch"]==1


def test_holdout_overlap_rejected_even_when_ids_and_seeds_differ():
    v={"id":"a","viewmat":np.eye(4).ravel().tolist(),"K":np.eye(3).ravel().tolist(),"width":16,"height":16}
    duplicate={**v,"id":"b","seed":999}
    with pytest.raises(ValueError,match="overlap"):
        validate_membership({"scoring":[v],"verify":[duplicate],"regression":[]})
    validate_membership({"scoring":[v],"verify":[],"regression":[]})


def test_tiny_round_runs_actual_scoring_and_holdout_verification_atomically(tmp_path):
    from splat_worker.jobs import run_job
    from splat_worker.source import inspect_source
    from tests.fixtures import write_ply
    from tests.test_scene_geometry import scene_fixture
    source=write_ply(tmp_path/"source.ply")[0]
    inspected=tmp_path/"inspected";inspect_source(source,inspected)
    (tmp_path/"current_inspect.json").write_text(json.dumps({"result_dir":str(inspected)}))
    scene_path=tmp_path/"scene.json";scene_path.write_text(json.dumps(scene_fixture()))
    directory=tmp_path/"jobs"/"tiny-round";directory.mkdir(parents=True)
    request={"schema_version":1,"job_id":"tiny-round","operation":"round1","source_path":str(source),
             "output_root":str(tmp_path),"scene_path":str(scene_path),
             "backend_cache_root":str(__import__('pathlib').Path(__file__).resolve().parents[3]/"SplatData"),
             "sampling":{"positions_per_set":[2,2,2],"heights":[.3],"size":32}}
    path=directory/"request.json";path.write_text(json.dumps(request))
    code=run_job(path)
    assert code==0,(directory/"error.log").read_text() if (directory/"error.log").exists() else "No successful round"
    result=tmp_path/"results"/"tiny-round"
    rank=json.loads((result/"rank_manifest.json").read_text())
    assert rank["source_count"]==4 and rank["eligible_count"]==3
    assert sorted(np.fromfile(result/"rank.bin",dtype="<u4").tolist())==[0,1,2]
    report=json.loads((result/"report.json").read_text())
    assert set(report["summary"])=={"100","75","50","25","0"}
    assert report["summary"]["100"]["max_rgb_mae"]==0
    assert report["view_counts"]=={"scoring":2,"verify":2,"regression":2}
    assert (tmp_path/"current_round1.json").exists()
