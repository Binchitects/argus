"""
A vLLM general plugin (entry point arena_gguf_rope in vllm.general_plugins, which vLLM loads in each of its processes) for
llama GGUF files. llama.cpp's converter reorders a llama model's query and key weights for its own rotary layout (GPT-J's,
each pair side by side), so its GGUF files hold them that way. vLLM's llama model used to turn its rotary to that layout
for such a file; 0.31's does not (GGUF moved out of its tree), and vllm-gguf-plugin 0.0.5 does not either. Without this a
llama GGUF answers, wrongly: the positions are scrambled (on SmolLM2, "One, two, three, four," goes on with " and", not
" five" as on llama.cpp). With it, a llama model read from a GGUF file takes GPT-J's layout, as on llama.cpp; nothing else
changes (a snapshot, or a GGUF file of another architecture: Qwen's are not reordered).

The images install it (services/vllm.Dockerfile, services/vllm-pypi.Dockerfile) beside vLLM, with its entry point; a change
here takes a new image (compose mounts services/vllm/, not this file).
"""
from __future__ import annotations


def register() -> None:
    from vllm.model_executor.layers.rotary_embedding import get_rope
    from vllm.model_executor.models import llama

    attention = llama.LlamaAttention
    if getattr(attention, "_arena_gguf_rope", False):
        return
    own = attention._init_rotary_emb

    def _init_rotary_emb(self, config, quant_config=None):
        own(self, config, quant_config=quant_config)
        if quant_config is not None and quant_config.get_name() == "gguf" and getattr(config, "model_type", None) == "llama":
            self.rotary_emb = get_rope(self.head_dim, max_position=self.max_position_embeddings,
                                       rope_parameters=getattr(config, "rope_parameters", None), is_neox_style=False)

    attention._init_rotary_emb = _init_rotary_emb
    attention._arena_gguf_rope = True
