using FundDca.Core.Rules;

namespace FundDca.Import.Parsing;

/// <summary>
/// 资产证明解析器：把用户主动导出的文件（PDF/Excel/CSV）在本地解析为统一的
/// <see cref="ParsedStatement"/>。原始文件不出本机，只在内存中读取。
/// </summary>
public interface IStatementParser
{
    /// <summary>能否处理该文件名（按扩展名）。</summary>
    bool Supports(string fileName);

    /// <summary>解析器标识（写入 ParsedStatement.Source 与日志）。</summary>
    string Source { get; }

    Task<ParsedStatement> ParseAsync(Stream content, string fileName, CancellationToken ct);
}

/// <summary>按文件名扩展名选择解析器（csv → xlsx → pdf）。</summary>
public sealed class StatementParserRegistry(IEnumerable<IStatementParser> parsers)
{
    private readonly List<IStatementParser> _parsers = parsers.ToList();

    public IStatementParser? Resolve(string fileName) =>
        _parsers.FirstOrDefault(p => p.Supports(fileName));
}
