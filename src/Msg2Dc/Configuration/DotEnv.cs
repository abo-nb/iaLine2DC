namespace Msg2Dc.Configuration;

public static class DotEnv
{
    public static void Load(string path, IConfigurationBuilder configuration)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var values = new Dictionary<string, string?>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"');
            values[key.Replace("__", ":")] = value;
        }

        configuration.AddInMemoryCollection(values);
    }
}
