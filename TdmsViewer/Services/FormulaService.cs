using System.Text.RegularExpressions;
using Jace;
using TdmsViewer.Models;

namespace TdmsViewer.Services;

/// <summary>
/// Evaluates calculated channels. Reference channels in expressions with
/// bracket syntax: <c>[Group/Channel] * 2 + 5</c>.
/// </summary>
public interface IFormulaService
{
    ChannelData Evaluate(string expression, Func<string, string, ChannelData> channelResolver);
}

public sealed partial class FormulaService : IFormulaService
{
    private readonly CalculationEngine _engine = new();

    public ChannelData Evaluate(string expression, Func<string, string, ChannelData> channelResolver)
    {
        var matches = ChannelTokenRegex().Matches(expression);

        var vars = new List<double[]>();
        var xTemplate = Array.Empty<double>();
        var xIsDateTime = false;
        var rewritten = expression;

        for (var i = 0; i < matches.Count; i++)
        {
            var token = matches[i].Groups[1].Value;
            var slash = token.LastIndexOf('/');
            if (slash < 0)
                throw new FormatException($"Channel reference must be [Group/Channel]: '{token}'.");

            var group = token[..slash].Trim();
            var name = token[(slash + 1)..].Trim();
            var data = channelResolver(group, name);
            vars.Add(data.Y);

            if (data.Y.Length > xTemplate.Length)
            {
                xTemplate = data.X;
                xIsDateTime = data.XIsDateTime;
            }

            rewritten = rewritten.Replace(matches[i].Value, $"v{i}");
        }

        var func = _engine.Build(rewritten);
        var length = vars.Count == 0 ? 0 : vars.Max(v => v.Length);
        var y = new double[length];
        var bag = new Dictionary<string, double>(vars.Count);

        for (var row = 0; row < length; row++)
        {
            for (var i = 0; i < vars.Count; i++)
                bag[$"v{i}"] = row < vars[i].Length ? vars[i][row] : double.NaN;
            try { y[row] = func(bag); }
            catch { y[row] = double.NaN; }
        }

        var x = xTemplate.Length == length
            ? xTemplate
            : Enumerable.Range(0, length).Select(i => (double)i).ToArray();

        return new ChannelData { X = x, Y = y, XIsDateTime = xIsDateTime && x == xTemplate };
    }

    [GeneratedRegex(@"\[([^\]]+)\]")]
    private static partial Regex ChannelTokenRegex();
}
