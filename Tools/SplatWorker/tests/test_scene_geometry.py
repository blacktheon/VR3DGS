import numpy as np
import pytest

from splat_worker.scene_geometry import (world_gaussians, floor_eligible,
    wall_visible, look_at, generate_views)


def scene_fixture():
    identity = np.eye(4).reshape(-1).tolist()
    return {"model_local_to_world": identity,
            "nav_vertices": [{"p": p} for p in [[-2,0,-2],[2,0,-2],[0,0,2],[-1,2,-1],[1,2,-1],[0,2,1]]],
            "nav_indices": [0,1,2,3,4,5], "nav_areas": [0,0],
            "walls": [{"name": "Floor", "floor": True, "normal": [0,1,0], "position": [0,0,0],
                       "world_to_local": identity, "bounds_min": [-1,0,-1], "bounds_max": [1,0,1]}]}


def test_source_reflection_applied_once_to_position_and_anisotropic_covariance():
    q = np.array([[np.cos(np.pi/8),0,np.sin(np.pi/8),0]])
    d = {"means": np.array([[1,2,3.]]), "quats": q, "scales": np.array([[1,2,3.]]),
         "opacities": np.array([.5]), "shs": np.zeros((1,1,3))}
    s = scene_fixture()
    m = np.diag([2.,2.,2.,1.]); m[:3,3] = [4,5,6]; s["model_local_to_world"] = m.ravel().tolist()
    result = world_gaussians(d,s)
    assert result["means"][0] == pytest.approx([6,9,0])
    assert np.linalg.eigvalsh(result["covars"][0]) == pytest.approx([4,16,36])
    assert result["covars"][0,0,2] == pytest.approx(-16)


def test_floor_exclusion_does_not_delete_boundary_or_above():
    assert floor_eligible(np.array([[99,-.001,99],[0,0,0],[0,.001,0]]),scene_fixture()).tolist() == [False,True,True]


def test_directional_wall_only_blocks_finite_rear_band_from_positive_side():
    s=scene_fixture(); s["walls"][0]["floor"]=False
    points=np.array([[0,-.05,0],[0,-.2,0],[0,.05,0],[5,-.05,0]])
    assert wall_visible(points,[0,1,0],s).tolist() == [False,True,True,True]
    assert wall_visible(points,[0,-1,0],s).tolist() == [True]*4
    # Ray crosses outside the rectangle even though the splat's projection is inside.
    assert wall_visible(np.array([[.99,-.1,0]]),[5,1,0],s).tolist() == [True]


def test_look_at_is_opencv_x_right_y_down_z_forward():
    matrix=look_at([0,1,-3],[0,1,0])
    assert matrix @ [0,1,0,1] == pytest.approx([0,0,3,1])
    assert matrix @ [1,2,0,1] == pytest.approx([1,-1,3,1])


def test_generated_views_cover_both_navmesh_levels_with_local_heights_and_disjoint_membership():
    s=scene_fixture()
    a=generate_views(s,positions_per_set=(8,4,2),size=64)
    b=generate_views(s,positions_per_set=(8,4,2),size=64)
    assert a==b
    all_keys=[]
    for name,views in a.items():
        assert {round(v["foot"][1]) for v in views} == {0,2}
        for v in views:
            assert .3-1e-7 <= v["eye"][1]-v["foot"][1] <= 1.8+1e-7
            # Membership in the independently authored triangular footprint.
            foot=np.array(v["foot"]); half=2 if foot[1]<1 else 1
            assert -half <= foot[2] <= half
            assert abs(foot[0]) <= (half-foot[2])/2+1e-6
            all_keys.append(tuple(np.round(np.r_[v["eye"],v["viewmat"]],6)))
    assert len(all_keys)==len(set(all_keys))
