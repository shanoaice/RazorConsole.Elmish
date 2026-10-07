# AGENTS prompt

## Rider Tooling

Do not assume Rider is always present.
When Rider MCP is available:

- Use Rider to trigger solution builds and execute run configurations.
- Use Rider to inspect and navigate F# code in `src/Pacpar.Alpm.FSharp`.
- Use Rider for interactive debugging and dotTrace profiling.
- Use Rider for solution-wide namespace adjustments and safe deletion.

When Rider MCP is absent, use standard `dotnet` CLI tools and SharpLens instead.

## Intermediate Outputs

- `.dsh-scratch/` — verification code, smoke tests, micro-benchmarks.
- `.dsh-refs/` — repositories cloned or downloaded for reference.
- `reports/` — the only folder for markdown work reports, unless explicitly asked otherwise.
- Use BenchmarkDotNet for all benchmarks. Stopwatch timing is not acceptable.

## Output Style

- 中文：尽量遵循受控中文技术写作的风格，使用适当长度的句子、主动语态、结论先行、适当分句，避免生硬翻译腔或过于凝练的词汇，禁用空泛词。
- English: Follow ASD-STE100 where possible. Active voice, one instruction per sentence. Use less than 20 words per sentence.
- 代码、命令和 API 名称一律保持原样。觉得冗长时只提建议，不要自行改动。清晰优先，不为简洁牺牲准确性。
