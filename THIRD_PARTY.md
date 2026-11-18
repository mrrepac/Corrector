# Сторонние компоненты

- **llama.cpp b12499**, MIT: https://github.com/ggml-org/llama.cpp/tree/b12499 . Загружается официальный Windows CUDA 12.4 runtime; лицензии поставщика сохраняются при распаковке.
- **Gemma 4 12B IT QAT**, Apache-2.0. [Исходная модель Google](https://huggingface.co/google/gemma-4-12B-it-qat-q4_0-unquantized).
- **GGUF UD-Q4_K_XL**, ревизия `980b060c40a8539ac159e0501a3e0f66a6365af3`. [Квантизация Unsloth](https://huggingface.co/unsloth/gemma-4-12b-it-qat-GGUF).
- **NVIDIA CUDA runtime / cuBLAS** входят в официальный архив llama.cpp, распространяются на условиях NVIDIA: https://docs.nvidia.com/cuda/eula/index.html .
- **.NET Windows Desktop runtime** включается в самостоятельную сборку, MIT и лицензии компонентов: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT .

Точные загружаемые файлы и контрольные суммы — `assets.lock.json`.
