"""Authored NavMesh views and explicit floor/directional-wall rules."""

import hashlib
import json
import math
import numpy as np


def scene_hash(scene):
    return hashlib.sha256(json.dumps(scene, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()).hexdigest()

def world_gaussians(decoded, scene):
    transform = np.asarray(scene["model_local_to_world"], dtype=np.float64).reshape(4,4)
    linear = transform[:3,:3] @ np.diag([1.,1.,-1.])
    q = decoded["quats"].astype(np.float64)
    q = q / np.linalg.norm(q,axis=1,keepdims=True)
    w,x,y,z = q.T
    rotation = np.stack([1-2*(y*y+z*z), 2*(x*y-z*w), 2*(x*z+y*w),
                         2*(x*y+z*w), 1-2*(x*x+z*z), 2*(y*z-x*w),
                         2*(x*z-y*w), 2*(y*z+x*w), 1-2*(x*x+y*y)],axis=1).reshape(-1,3,3)
    axes = (linear @ rotation) * decoded["scales"][:,None,:]
    return {"means": (decoded["means"] @ linear.T + transform[:3,3]).astype(np.float32),
            "covars": (axes @ axes.transpose(0,2,1)).astype(np.float32),
            "opacities": decoded["opacities"].astype(np.float32),
            "colors": np.maximum(0., .5 + .28209479177387814 * decoded["shs"][:,0,:]).astype(np.float32)}

def floor_eligible(means, scene):
    eligible = np.ones(len(means),dtype=bool)
    floors = [w for w in scene["walls"] if w["floor"]]
    if len(floors)!=1:
        raise ValueError("Exactly one authored Floor plane is required")
    for wall in floors:
        eligible &= (np.asarray(means)-wall["position"]) @ np.asarray(wall["normal"]) >= 0
    return eligible

def wall_visible(means, eye, scene, rear_depth=0.1):
    if not 0 < rear_depth <= .1:
        raise ValueError("Rear band must be greater than zero and at most 0.1")
    means=np.asarray(means); eye=np.asarray(eye,dtype=float)
    visible=np.ones(len(means),dtype=bool)
    for wall in scene["walls"]:
        if wall["floor"]: continue
        n=np.asarray(wall["normal"]); p=np.asarray(wall["position"])
        side=float((eye-p) @ n)
        if side <= 1e-6: continue
        distance=(means-p) @ n
        ids=np.flatnonzero((distance>=-rear_depth)&(distance<0)&visible)
        if not len(ids): continue
        t=side/(side-distance[ids])
        hit=eye+(means[ids]-eye)*t[:,None]
        inverse=np.asarray(wall["world_to_local"]).reshape(4,4)
        local=hit @ inverse[:3,:3].T+inverse[:3,3]
        lo,hi=np.asarray(wall["bounds_min"]),np.asarray(wall["bounds_max"])
        inside=(local[:,0]>=lo[0])&(local[:,0]<=hi[0])&(local[:,2]>=lo[2])&(local[:,2]<=hi[2])
        visible[ids[inside]]=False
    return visible

def look_at(eye, target):
    eye=np.asarray(eye,dtype=float); forward=np.asarray(target,dtype=float)-eye
    length=np.linalg.norm(forward)
    if length<1e-8: raise ValueError("Eye and target coincide")
    forward/=length
    up=np.array([0.,1.,0.]) if abs(forward[1])<.99 else np.array([0.,0.,1.])
    right=np.cross(up,forward);right/=np.linalg.norm(right)
    down=np.cross(right,forward)
    matrix=np.eye(4);matrix[:3,:3]=[right,down,forward];matrix[:3,3]=-matrix[:3,:3] @ eye
    return matrix

def generate_views(scene, positions_per_set=(48, 8, 4), heights=(0.3, 0.8, 1.3, 1.8), size=640):
    vertices=np.asarray([v["p"] for v in scene["nav_vertices"]],dtype=float)
    triangles=vertices[np.asarray(scene["nav_indices"]).reshape(-1,3)]
    areas=np.linalg.norm(np.cross(triangles[:,1]-triangles[:,0],triangles[:,2]-triangles[:,0]),axis=1)/2
    good=areas>1e-8; triangles,areas=triangles[good],areas[good]
    if not len(triangles): raise ValueError("NavMesh has no nondegenerate triangles")
    # This scene has two flat levels. Stratify each; do not interpolate through
    # the unsupported vertical space between their walkable surfaces.
    level=np.round(triangles[:,:,1].mean(axis=1),1)
    groups=[np.flatnonzero(level==value) for value in np.unique(level)]
    if any(h<.3 or h>1.8 for h in heights): raise ValueError("Eye height outside authored 0.3–1.8 range")
    walls=[w for w in scene["walls"] if not w["floor"]]
    center=np.mean([w["position"] for w in walls],axis=0) if walls else np.mean(vertices,axis=0)
    center[1]=max(.8,float(center[1]))
    targets=[np.asarray(w["position"],dtype=float) for w in walls] or [center]
    f=size/(2*math.tan(math.radians(75)/2))
    sets={}; used=set()
    for label,seed,count in zip(("scoring","verify","regression"),(11,29,47),positions_per_set):
        rng=np.random.default_rng(seed);views=[]
        for index in range(count):
            group=groups[index%len(groups)]
            triangle_id=int(rng.choice(group,p=areas[group]/areas[group].sum()))
            u,v=rng.random(2);root=math.sqrt(u)
            foot=(1-root)*triangles[triangle_id,0]+root*(1-v)*triangles[triangle_id,1]+root*v*triangles[triangle_id,2]
            for hindex,h in enumerate(heights):
                eye=foot+[0,h,0]
                target=center if hindex%2==0 else targets[index%len(targets)]
                if np.linalg.norm(target-eye)<.1: target=center+[0,.1,.1]
                matrix=look_at(eye,target)
                key=tuple(np.round(np.r_[eye,matrix.ravel()],6))
                if key in used: raise ValueError("Generated view membership overlaps")
                used.add(key)
                views.append({"id":f"{label}-{index:03d}-{hindex}","membership":label,"seed":seed,
                              "foot":foot.tolist(),"eye":eye.tolist(),"eye_height":h,"nav_triangle":triangle_id,
                              "viewmat":matrix.ravel().tolist(),"K":[f,0,size/2,0,f,size/2,0,0,1],
                              "width":size,"height":size,"near":.03,"far":100.,"priority":1.})
        sets[label]=views
    return sets
