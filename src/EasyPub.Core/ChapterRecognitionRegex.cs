using System.Text.RegularExpressions;

namespace EasyPub.Core;

/// <summary>区分章节规则配置错误与正文没有匹配项。</summary>
public enum ChapterRecognitionRuleFailure
{
    InvalidPattern,
    Timeout,
}

/// <summary>
/// A user supplied chapter rule failed before it could produce a trustworthy result.
/// The editor can show this message directly without treating the book as having no chapters.
/// </summary>
public sealed class ChapterRecognitionRuleException : ArgumentException
{
    public ChapterRecognitionRuleException(
        string ruleName,
        ChapterRecognitionRuleFailure failure,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        RuleName = ruleName;
        Failure = failure;
    }

    public string RuleName { get; }

    public ChapterRecognitionRuleFailure Failure { get; }
}

/// <summary>
/// The one compilation and matching seam used by chapter recognition paths.
/// A bounded timeout is part of the contract: a pathological expression must fail loudly,
/// never turn a long book into a misleading "0 chapters" result.
/// </summary>
public static class ChapterRecognitionRegex
{
    public const int MatchTimeoutMilliseconds = 200;

    public static Regex Compile(string? pattern, string fallback, string ruleName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallback);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleName);
        var effective = string.IsNullOrWhiteSpace(pattern) ? fallback : pattern;
        try
        {
            return new Regex(
                effective,
                RegexOptions.Compiled | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(MatchTimeoutMilliseconds));
        }
        catch (RegexParseException error)
        {
            throw Invalid(ruleName, error);
        }
        catch (ArgumentException error)
        {
            // Regex constructors use ArgumentException for a few invalid option/pattern cases
            // that are not represented by RegexParseException on every supported runtime.
            throw Invalid(ruleName, error);
        }
    }

    public static bool IsMatch(Regex regex, string input, string ruleName, int? lineNumber = null)
    {
        ArgumentNullException.ThrowIfNull(regex);
        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException error)
        {
            throw Timeout(ruleName, lineNumber, error);
        }
    }

    public static Match Match(Regex regex, string input, string ruleName, int? lineNumber = null)
    {
        ArgumentNullException.ThrowIfNull(regex);
        try
        {
            return regex.Match(input);
        }
        catch (RegexMatchTimeoutException error)
        {
            throw Timeout(ruleName, lineNumber, error);
        }
    }

    private static ChapterRecognitionRuleException Invalid(string ruleName, Exception error) =>
        new(
            ruleName,
            ChapterRecognitionRuleFailure.InvalidPattern,
            $"规则“{ruleName}”不是有效的正则表达式：{error.Message}",
            error);

    private static ChapterRecognitionRuleException Timeout(
        string ruleName,
        int? lineNumber,
        Exception error)
    {
        var location = lineNumber is { } line ? $"（原文第 {line} 行）" : string.Empty;
        return new ChapterRecognitionRuleException(
            ruleName,
            ChapterRecognitionRuleFailure.Timeout,
            $"规则“{ruleName}”处理{location}时超过 {MatchTimeoutMilliseconds} 毫秒，已停止识别。请简化正则或缩小匹配范围。",
            error);
    }
}
