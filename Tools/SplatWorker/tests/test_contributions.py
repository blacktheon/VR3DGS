from pathlib import Path
import numpy as np
import pytest

from splat_worker.backends.contributions import composite_weights, load_accumulator, accumulate_projected


def test_transmittance_clamp_cutoff_and_exclusive_termination():
    assert composite_weights([.8,.8,.8]) == pytest.approx([.8,.16,.032],abs=1e-6)
    assert composite_weights([.001,.8]) == pytest.approx([0,.8])
    assert composite_weights([1.,1.]) == pytest.approx([.999,0])


@pytest.fixture(scope="module")
def gpu_backend():
    import torch
    from splat_worker.environment import configure_toolchain
    from splat_worker.backends.gsplat_backend import load_cuda_backend
    import os
    output=Path(__file__).resolve().parents[3]/"SplatData"
    configure_toolchain(output)
    capability=torch.cuda.get_device_capability()
    os.environ["TORCH_CUDA_ARCH_LIST"]=f"{capability[0]}.{capability[1]}"
    load_cuda_backend(output)
    return output


@pytest.mark.parametrize("source_order,source_count", [([0,1,2,3],4), ([7,2,11,4],13)])
def test_gpu_contributions_match_stock_image_and_independent_pixel_oracle(gpu_backend,source_order,source_count):
    import torch
    from gsplat import rasterization
    gpu_backend=load_accumulator(gpu_backend)
    means=torch.tensor([[0,0,2.],[0,0,3.],[0,0,4.],[99,0,2.]],device="cuda")
    colors=torch.tensor([[1.,0,0],[0,1.,0],[0,0,1.],[1,1,1]],device="cuda")
    rgb,alpha,meta=rasterization(means=means,quats=torch.tensor([[1.,0,0,0]]*4,device="cuda"),
        scales=torch.tensor([[.2,.2,.2],[.3,.3,.3],[.4,.4,.4],[.1,.1,.1]],device="cuda"),
        opacities=torch.tensor([.8,.8,.8,.8],device="cuda"),colors=colors,
        viewmats=torch.eye(4,device="cuda")[None],Ks=torch.tensor([[[20.,0,8.5],[0,20.,8.5],[0,0,1.]]],device="cuda"),
        width=17,height=17,packed=True,radius_clip=0,rasterize_mode="classic")
    ids=meta["gaussian_ids"].long()
    original_ids=torch.tensor(source_order,device="cuda",dtype=torch.int64)[ids]
    out=accumulate_projected(gpu_backend,meta,colors[ids],original_ids,source_count)
    assert torch.max(torch.abs(out["rgb"]-rgb)).item()<1e-5
    assert torch.max(torch.abs(out["alpha"]-alpha)).item()<1e-5
    xy=meta["means2d"].cpu().numpy();conics=meta["conics"].cpu().numpy();ops=meta["opacities"].cpu().numpy()
    expected=np.zeros((source_count,17,17))
    for y in range(17):
        for x in range(17):
            T=1.
            for g,source_id in enumerate(original_ids.cpu().numpy()):
                dx,dy=xy[g]-[x+.5,y+.5];a,b,c=conics[g]
                sigma=.5*(a*dx*dx+c*dy*dy)+b*dx*dy
                opacity=min(.999,float(ops[g])*np.exp(-sigma))
                if sigma<0 or opacity<1/255: continue
                next_T=T*(1-opacity)
                if next_T<=1e-4: break
                expected[source_id,y,x]=T*opacity;T=next_T
    np.testing.assert_allclose(out["peak"].cpu(),expected.max(axis=(1,2)),atol=1e-5)
    np.testing.assert_allclose(out["sum_fixed"].cpu().numpy()/2**24,expected.sum(axis=(1,2)),atol=3e-5)
    unused=sorted(set(range(source_count))-set(source_order[:3]))
    assert torch.count_nonzero(out["hits"][unused]).item()==0
    assert torch.count_nonzero(out["sum_fixed"][unused]).item()==0
    # Repeat integer aggregation must not depend on CUDA thread scheduling.
    again=accumulate_projected(gpu_backend,meta,colors[ids],original_ids,source_count)
    assert torch.equal(out["sum_fixed"],again["sum_fixed"])
