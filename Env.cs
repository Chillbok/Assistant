namespace Assistant.Bot;

internal static class Env
{
    public static string Require(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is not set");
        }

        return value.Trim();
    }

    public static HashSet<long> IdSet(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name) ?? string.Empty;
        var ids = new HashSet<long>();

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (long.TryParse(part, out var id))
            {
                ids.Add(id);
            }
            else
            {
                Console.Error.WriteLine($"[warn] {name}: ignoring invalid id '{part}'");
            }
        }

        return ids;
    }
}
