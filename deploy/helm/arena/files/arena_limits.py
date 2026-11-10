"""
The gateway's hold on each request's longest answer (litellm.yaml's callbacks): a request that asks more than its
model's longest answer (its max_output_tokens, as the app registers it) is lowered to it, however it asks:
max_tokens (chat completions, text completions, Anthropic's messages), max_completion_tokens (chat completions) or
max_output_tokens (the Responses API). llama.cpp writes as long as a request asks, until its slot is full;
LiteLLM's own modify_params lowers only max_tokens, and only on chat completions.
"""
from __future__ import annotations

from litellm.integrations.custom_logger import CustomLogger

# Every name a request gives its longest answer by.
LIMITS = ("max_tokens", "max_completion_tokens", "max_output_tokens")


def longest(deployments):
    """A model's longest answer: the least its copies say (max_output_tokens); None when none says one."""
    said = [(d.get("model_info") or {}).get("max_output_tokens") for d in deployments or []]
    said = [n for n in said if isinstance(n, int) and not isinstance(n, bool) and n > 0]
    return min(said) if said else None


def held(data, most):
    """The request with each longest answer it asks above most lowered to most."""
    for key in LIMITS:
        asked = data.get(key)
        if isinstance(asked, int) and not isinstance(asked, bool) and asked > most:
            data[key] = most
    return data


class LongestAnswer(CustomLogger):
    async def async_pre_call_hook(self, user_api_key_dict, cache, data, call_type):
        from litellm.proxy.proxy_server import llm_router

        model = data.get("model")
        if llm_router is None or not isinstance(model, str):
            return data
        most = longest(llm_router.get_model_list(model_name=model))
        return data if most is None else held(data, most)


longest_answer = LongestAnswer()
