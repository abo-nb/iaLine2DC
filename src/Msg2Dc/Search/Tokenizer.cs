using System.Text;

namespace Msg2Dc.Search;

public static class Tokenizer
{
    private static readonly char[] Separators =
    {
        ' ', '\t', '\n', '\r', '，', '。', '、', '；', '：', '？', '！', '「', '」', '『', '』', '（', '）',
        '(', ')', ',', '.', ';', ':', '?', '!', '"', '\'', '@', '#', '$', '%', '&', '*', '+', '-', '=', '/',
        '\\', '|', '~', '<', '>', '[', ']', '{', '}', '《', '》', '…', '—', '·'
    };

    public static List<string> Tokenize(string? text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new List<string>();
        }

        foreach (var chunk in text.ToLowerInvariant().Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var cjkRun = new StringBuilder();
            foreach (var ch in chunk)
            {
                if (IsCjk(ch))
                {
                    cjkRun.Append(ch);
                    continue;
                }

                if (cjkRun.Length > 0)
                {
                    AddCjkBigrams(cjkRun.ToString(), tokens);
                    cjkRun.Clear();
                }
            }

            if (cjkRun.Length > 0)
            {
                AddCjkBigrams(cjkRun.ToString(), tokens);
            }

            if (chunk.Length > 1 && chunk.All(c => !IsCjk(c)))
            {
                tokens.Add(chunk);
            }
        }

        return tokens.ToList();
    }

    private static void AddCjkBigrams(string run, HashSet<string> tokens)
    {
        if (run.Length == 1)
        {
            tokens.Add(run);
            return;
        }

        for (var i = 0; i < run.Length - 1; i++)
        {
            tokens.Add(run.Substring(i, 2));
        }
    }

    private static bool IsCjk(char ch) =>
        (ch >= 0x4E00 && ch <= 0x9FFF) || (ch >= 0x3400 && ch <= 0x4DBF) || (ch >= 0xF900 && ch <= 0xFAFF);
}
