# ClaudeAgent 网关 Python OpenAI SDK 交叉验证（可选，额度消耗最小化：自动选 Haiku）
# 用法：pip install openai 后执行 python scripts/verify_python.py
import sys

from openai import OpenAI

BASE = "http://127.0.0.1:5055/v1"
client = OpenAI(base_url=BASE, api_key="sk-claude")


def pick_haiku():
    models = client.models.list()
    ids = [m.id for m in models.data]
    print(f"[1/4] /v1/models -> {len(ids)} models")
    for mid in ids:
        print("   -", mid)
    haiku = next((mid for mid in ids if "haiku" in mid.lower()), ids[0] if ids else None)
    if not haiku:
        print("!! no models available")
        sys.exit(1)
    print(f"     using haiku model: {haiku}")
    return haiku


def main():
    model = pick_haiku()

    # 非流式问答（claude 5.x 的 adaptive thinking 会消耗输出预算，需放宽 max_tokens）
    completion = client.chat.completions.create(
        model=model,
        messages=[{"role": "user", "content": "9.11 和 9.9 谁更大？请仅输出更大的那个数字。"}],
        max_tokens=512,
    )
    text = completion.choices[0].message.content or ""
    print(f"[2/4] non-stream -> {text!r}")
    assert "9.9" in text, "non-stream answer wrong"
    assert completion.usage.prompt_tokens > 0 and completion.usage.completion_tokens > 0

    # 流式
    stream = client.chat.completions.create(
        model=model,
        messages=[{"role": "user", "content": "从 1 数到 3，用空格分隔"}],
        stream=True,
        max_tokens=256,
    )
    parts, got_text = [], False
    for chunk in stream:
        if chunk.choices and chunk.choices[0].delta.content:
            parts.append(chunk.choices[0].delta.content)
            got_text = True
    joined = "".join(parts)
    print(f"[3/4] stream -> {joined!r} (chunks ok: {got_text})")
    assert got_text, "stream produced no content"

    # 工具调用
    tools = [{
        "type": "function",
        "function": {
            "name": "get_stock_price",
            "description": "Get real-time stock price for a ticker",
            "parameters": {
                "type": "object",
                "properties": {"ticker": {"type": "string", "description": "Stock ticker"}},
                "required": ["ticker"],
            },
        },
    }]
    first = client.chat.completions.create(
        model=model,
        messages=[{"role": "user", "content": "查询谷歌 (GOOG) 当前股价，必须调用工具"}],
        tools=tools,
        max_tokens=1024,
    )
    calls = first.choices[0].message.tool_calls
    assert calls and calls[0].function.name == "get_stock_price", "tool call missing"
    print(f"[4/4] tool call -> {calls[0].function.name}({calls[0].function.arguments})")

    # 多轮：回传工具结果
    import json
    second = client.chat.completions.create(
        model=model,
        messages=[
            {"role": "user", "content": "查询谷歌 (GOOG) 当前股价，必须调用工具"},
            {"role": "assistant", "content": None, "tool_calls": [
                {"id": calls[0].id, "type": "function",
                 "function": {"name": calls[0].function.name, "arguments": calls[0].function.arguments}}
            ]},
            {"role": "tool", "tool_call_id": calls[0].id, "content": "GOOG: $205.30"},
        ],
        tools=tools,
        max_tokens=1024,
    )
    final_text = second.choices[0].message.content or ""
    print(f"     multi-turn -> {final_text!r}")
    assert "205" in final_text, "multi-turn result wrong"

    print("\nALL PYTHON SDK CHECKS PASSED ✅")


if __name__ == "__main__":
    main()
