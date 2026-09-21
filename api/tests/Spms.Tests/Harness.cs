namespace Spms.Tests;

/// <summary>
/// A minimal assertion harness.
///
/// xUnit would be the obvious choice, but nuget.org is unreachable from this
/// build environment and a test suite that cannot run is worth nothing.
/// Swap to xUnit when the pipeline has a feed — the test bodies port directly.
/// </summary>
public static class Harness
{
    private static int _passed, _failed;
    private static readonly List<string> Failures = [];

    public static void Check(string name, bool condition, string? detail = null)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  PASS  {name}");
        }
        else
        {
            _failed++;
            var line = detail is null ? name : $"{name} — {detail}";
            Failures.Add(line);
            Console.WriteLine($"  FAIL  {line}");
        }
    }

    public static void Equal<T>(string name, T expected, T actual) =>
        Check(name, EqualityComparer<T>.Default.Equals(expected, actual),
              $"expected {expected}, got {actual}");

    public static void Section(string title) => Console.WriteLine($"\n{title}");

    public static int Summarise()
    {
        Console.WriteLine($"\n{_passed} passed, {_failed} failed.");
        foreach (var f in Failures) Console.WriteLine($"  - {f}");
        return _failed == 0 ? 0 : 1;
    }
}
