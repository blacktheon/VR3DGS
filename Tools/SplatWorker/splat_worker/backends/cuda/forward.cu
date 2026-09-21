// Adapted from gsplat v1.5.3 RasterizeToPixels3DGSFwd.cu.
// Copyright the gsplat authors. Licensed under Apache-2.0; see LICENSE.gsplat.
// Changes: single packed RGB camera, original-source addressing, peak and
// 24-bit fixed-point contribution accumulation. Projection/order stay upstream.
#include <ATen/ATen.h>
#include <c10/cuda/CUDAStream.h>
#include <c10/cuda/CUDAGuard.h>
#include <c10/cuda/CUDAException.h>
#include <cuda_runtime.h>
#include <vector>

__global__ void accumulate_kernel(const float2* means, const float3* conics,
    const float* colors, const float* opacity, const int* offsets,
    const int* ids, const int64_t* source_ids, int intersections, int width,
    int height, int tile, int tiles_x, int tiles_y, float* rgb, float* alpha_out,
    float* peak, unsigned long long* sums, unsigned long long* hits) {
    int x=blockIdx.x*tile+threadIdx.x, y=blockIdx.y*tile+threadIdx.y;
    int tile_id=blockIdx.y*tiles_x+blockIdx.x;
    int start=offsets[tile_id];
    int end=(tile_id==tiles_x*tiles_y-1)?intersections:offsets[tile_id+1];
    int thread=threadIdx.y*tile+threadIdx.x, block_size=tile*tile;
    bool inside=x<width && y<height, done=!inside;
    __shared__ int shared_ids[256];
    __shared__ float3 shared_xy_opacity[256], shared_conics[256];
    float T=1.f, color[3]={0.f,0.f,0.f};
    for(int batch=start;batch<end;batch+=block_size) {
        if(__syncthreads_count(done)==block_size) break;
        int index=batch+thread;
        if(index<end) {
            int g=ids[index]; float2 xy=means[g];
            shared_ids[thread]=g;
            shared_xy_opacity[thread]=make_float3(xy.x,xy.y,opacity[g]);
            shared_conics[thread]=conics[g];
        }
        __syncthreads();
        int length=min(block_size,end-batch);
        for(int j=0;j<length && !done;j++) {
            float3 xy=shared_xy_opacity[j], q=shared_conics[j];
            float dx=xy.x-(float(x)+.5f), dy=xy.y-(float(y)+.5f);
            float sigma=.5f*(q.x*dx*dx+q.z*dy*dy)+q.y*dx*dy;
            float alpha=min(.999f,xy.z*__expf(-sigma));
            if(sigma<0.f || alpha<1.f/255.f) continue;
            float next_T=T*(1.f-alpha);
            if(next_T<=1e-4f) {done=true;break;}
            int g=shared_ids[j]; int64_t source=source_ids[g];
            float contribution=alpha*T;
            #pragma unroll
            for(int channel=0;channel<3;channel++) color[channel]+=colors[g*3+channel]*contribution;
            atomicMax(reinterpret_cast<unsigned int*>(peak+source),__float_as_uint(contribution));
            atomicAdd(sums+source,(unsigned long long)floorf(contribution*16777216.f+.5f));
            atomicAdd(hits+source,1ULL);
            T=next_T;
        }
    }
    if(inside) {
        int pixel=y*width+x; alpha_out[pixel]=1.f-T;
        #pragma unroll
        for(int channel=0;channel<3;channel++) rgb[pixel*3+channel]=color[channel];
    }
}

std::vector<at::Tensor> stage1_forward(at::Tensor means, at::Tensor conics,
    at::Tensor colors, at::Tensor opacity, at::Tensor offsets, at::Tensor ids,
    at::Tensor source_ids, int64_t source_count, int64_t width, int64_t height, int64_t tile) {
    TORCH_CHECK(tile==16 && width>0 && height>0 && source_count>=0,"Invalid contribution dimensions");
    for(auto t:{means,conics,colors,opacity,offsets,ids,source_ids}) {
        TORCH_CHECK(t.is_cuda() && t.is_contiguous() && t.device()==means.device(),"Expected contiguous tensors on one CUDA device");
    }
    TORCH_CHECK(means.scalar_type()==at::kFloat && conics.scalar_type()==at::kFloat && colors.scalar_type()==at::kFloat && opacity.scalar_type()==at::kFloat,"Expected float32 projected attributes");
    TORCH_CHECK(offsets.scalar_type()==at::kInt && ids.scalar_type()==at::kInt && source_ids.scalar_type()==at::kLong,"Expected int32 tile IDs and int64 source IDs");
    TORCH_CHECK(means.dim()==2 && means.size(1)==2 && conics.size(0)==means.size(0) && conics.size(1)==3 && colors.size(0)==means.size(0) && colors.size(1)==3 && opacity.numel()==means.size(0) && source_ids.numel()==means.size(0),"Projected attribute shapes disagree");
    int tiles_x=(width+tile-1)/tile, tiles_y=(height+tile-1)/tile;
    TORCH_CHECK(offsets.numel()==tiles_x*tiles_y,"Tile count disagrees with image");
    c10::cuda::CUDAGuard guard(means.device());
    auto rgb=at::zeros({1,height,width,3},means.options());
    auto alpha=at::zeros({1,height,width,1},means.options());
    auto peak=at::zeros({source_count},means.options());
    auto sums=at::zeros({source_count},means.options().dtype(at::kLong));
    auto hits=at::zeros({source_count},means.options().dtype(at::kLong));
    accumulate_kernel<<<dim3(tiles_x,tiles_y),dim3(tile,tile),0,c10::cuda::getCurrentCUDAStream()>>>(
        reinterpret_cast<float2*>(means.data_ptr<float>()),reinterpret_cast<float3*>(conics.data_ptr<float>()),
        colors.data_ptr<float>(),opacity.data_ptr<float>(),offsets.data_ptr<int>(),ids.data_ptr<int>(),
        source_ids.data_ptr<int64_t>(),ids.numel(),width,height,tile,tiles_x,tiles_y,
        rgb.data_ptr<float>(),alpha.data_ptr<float>(),peak.data_ptr<float>(),
        reinterpret_cast<unsigned long long*>(sums.data_ptr<int64_t>()),reinterpret_cast<unsigned long long*>(hits.data_ptr<int64_t>()));
    C10_CUDA_KERNEL_LAUNCH_CHECK();
    return {rgb,alpha,peak,sums,hits};
}
