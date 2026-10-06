using System.Text;

namespace TdmsViewer;

/// <summary>One term of a data-portal filter: a literal substring, optionally negated or anchored to the whole field.</summary>
public sealed class FilterTerm
{
    public FilterTerm(string text, bool negate, bool exact)
    {
        Text = text;
        Negate = negate;
        Exact = exact;
    }

    public string Text { get; }
    public bool Negate { get; }
    public bool Exact { get; }

    public bool Matches(string value) => Exact
        ? string.Equals(value, Text, StringComparison.OrdinalIgnoreCase)
        : value.Contains(Text, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Parsed data-portal filter. Supports <c>"quoted phrases"</c>, <c>=exact</c> whole-field matches,
/// <c>-excluded</c> terms, and a <c>Group/Channel</c> path separator that scopes each side.
/// </summary>
public sealed class FilterQuery
{
    private static readonly FilterQuery EmptyQuery = new(Array.Empty<FilterTerm>(), null);

    private FilterQuery(IReadOnlyList<FilterTerm> terms, IReadOnlyList<FilterTerm>? channelTerms)
    {
        Terms = terms;
        ChannelTerms = channelTerms;
    }

    /// <summary>Terms before the <c>/</c>, or all terms when no separator was typed.</summary>
    public IReadOnlyList<FilterTerm> Terms { get; }

    /// <summary>Terms after the <c>/</c>; null when the user did not type a path separator.</summary>
    public IReadOnlyList<FilterTerm>? ChannelTerms { get; }

    public bool IsScoped => ChannelTerms is not null;

    public bool IsEmpty => Terms.Count == 0 && (ChannelTerms is null || ChannelTerms.Count == 0);

    /// <summary>Literal strings that should be highlighted in the tree (negated and exact terms excluded).</summary>
    public IReadOnlyList<string> HighlightTerms
    {
        get
        {
            var list = new List<string>();
            foreach (var t in Terms)
                if (!t.Negate && !t.Exact && t.Text.Length > 0) list.Add(t.Text);
            if (ChannelTerms is not null)
                foreach (var t in ChannelTerms)
                    if (!t.Negate && !t.Exact && t.Text.Length > 0) list.Add(t.Text);
            return list;
        }
    }

    public static FilterQuery Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return EmptyQuery;

        var left = new List<FilterTerm>();
        List<FilterTerm>? right = null;
        var target = left;

        var buffer = new StringBuilder();
        var negate = false;
        var exact = false;
        var started = false;

        void Flush()
        {
            if (buffer.Length > 0)
                target.Add(new FilterTerm(buffer.ToString(), negate, exact));
            buffer.Clear();
            negate = false;
            exact = false;
            started = false;
        }

        var inQuotes = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                // Quotes keep spaces together; an unterminated quote simply runs to the end of the text.
                inQuotes = !inQuotes;
                started = true;
                continue;
            }

            if (!inQuotes)
            {
                if (char.IsWhiteSpace(c)) { Flush(); continue; }

                if (c == '/')
                {
                    Flush();
                    if (right is null)
                    {
                        right = new List<FilterTerm>();
                        target = right;
                    }
                    continue;
                }

                if (!started)
                {
                    if (c == '-') { negate = true; started = true; continue; }
                    if (c == '=') { exact = true; started = true; continue; }
                }
                else if (buffer.Length == 0 && negate && !exact && c == '=')
                {
                    exact = true;
                    continue;
                }
            }

            started = true;
            buffer.Append(c);
        }
        Flush();

        return left.Count == 0 && right is null ? EmptyQuery : new FilterQuery(left, right);
    }

    /// <summary>
    /// Decides which channels of a group survive the filter.
    /// Returns false when the whole group is filtered out; otherwise <paramref name="remaining"/> holds the
    /// terms the group itself did not satisfy, which the channel names must then match.
    /// </summary>
    public bool TryScopeToGroup(string group, out IReadOnlyList<FilterTerm> remaining)
    {
        remaining = Array.Empty<FilterTerm>();
        if (IsEmpty) return true;

        if (IsScoped)
        {
            foreach (var t in Terms)
                if (t.Matches(group) == t.Negate) return false;
            remaining = ChannelTerms!;
            return true;
        }

        List<FilterTerm>? unmatched = null;
        foreach (var t in Terms)
        {
            // Negated terms apply to both fields and are never satisfied by the group alone.
            if (t.Negate)
            {
                if (t.Matches(group)) return false;
                (unmatched ??= new List<FilterTerm>()).Add(t);
            }
            else if (!t.Matches(group))
            {
                (unmatched ??= new List<FilterTerm>()).Add(t);
            }
        }

        remaining = (IReadOnlyList<FilterTerm>?)unmatched ?? Array.Empty<FilterTerm>();
        return true;
    }

    public static bool MatchesChannel(IReadOnlyList<FilterTerm> terms, string name)
    {
        foreach (var t in terms)
            if (t.Matches(name) == t.Negate) return false;
        return true;
    }
}
