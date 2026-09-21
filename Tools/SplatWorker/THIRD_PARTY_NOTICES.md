# Worker dependencies

The local worker uses gsplat 1.5.3 at commit `937e29912570c372bed6747a5c9bf85fed877bae`, distributed under Apache-2.0 by the gsplat authors: https://github.com/nerfstudio-project/gsplat/tree/937e29912570c372bed6747a5c9bf85fed877bae.

`splat_worker/backends/gsplat_backend.py` compiles the installed, unchanged CUDA/C++ sources using PyTorch's public extension loader. It replaces only the upstream JIT loader's incompatible Windows host flags; no rasterization algorithm is changed. The installed package includes the upstream license and third-party notices. Preserve those when redistributing the worker environment.

PyTorch, NumPy, psutil, Ninja and the other installed dependencies retain their respective licenses and notices in the environment. `requirements.lock` records the verified versions. This environment is local authoring tooling and is not included in Unity player builds.

The Unity renderer is a separate project, `wuyize25/gsplat-unity`, pinned at `a2bf458d6b16395e6570e9345f9f4408f92684b8` under MIT. Its copyright and license are retained in the installed Unity package.
