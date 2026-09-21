// Stage 1 forward contribution instrumentation, project implementation.
#include <torch/extension.h>
#include <vector>

std::vector<at::Tensor> stage1_forward(at::Tensor means, at::Tensor conics,
    at::Tensor colors, at::Tensor opacity, at::Tensor offsets, at::Tensor ids,
    at::Tensor source_ids, int64_t source_count, int64_t width, int64_t height, int64_t tile);

PYBIND11_MODULE(TORCH_EXTENSION_NAME, module) {
    module.def("forward", &stage1_forward, "Front-to-back contribution accumulation");
}
