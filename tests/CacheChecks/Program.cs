using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

using CheapLoc;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
}
var first = new CountingAssembly("CacheChecks.First");
Check(Loc.Localize("Key", "Fallback", first) == "#Key", "Unconfigured assembly");
Loc.SetupWithFallbacks(first);
Check(Loc.Localize("Key", "Fallback", first) == "Fallback", "Configured fallback");
Check(Loc.Localize("Missing", "", first) == "#Missing", "Missing empty fallback");
Loc.Setup("""{"Key":{"message":"First"},"Empty":{"message":""}}""", first);
Check(Loc.Localize("Key", "Fallback", first) == "First", "Translation");
Check(Loc.Localize("Empty", "Fallback", first) == "#Empty", "Empty translation");
Check(Loc.Localize("Missing", "One", first) == "One" && Loc.Localize("Missing", "Two", first) == "Two", "Current fallback argument");
Loc.Setup("""{"Key":{"message":"Changed"}}""", first);
Check(Loc.Localize("Key", "Fallback", first) == "Changed", "Language replacement");
Check(first.NameReads > 1, "Custom assembly overrides remain live");

var second = new CountingAssembly("CacheChecks.Second");
Loc.Setup("""{"Key":{"message":"Second"}}""", second);
Check(Loc.Localize("Key", "Fallback", first) == "Changed" && Loc.Localize("Key", "Fallback", second) == "Second", "Assembly isolation");
var sameName = new CountingAssembly("CacheChecks.First");
Loc.Setup("""{"Key":{"message":"Shared name"}}""", sameName);
Check(Loc.Localize("Key", "Fallback", first) == "Shared name", "Existing same-name ownership semantics");
first.SimpleName = "CacheChecks.Second";
Check(Loc.Localize("Key", "Fallback", first) == "Second", "Mutable custom assembly name");
first.SimpleName = "CacheChecks.First";
try
{
    _ = Loc.Localize("Key", "Fallback", null!);
    throw new Exception("Null assembly was accepted");
}
catch (NullReferenceException)
{
    Check(true, "Legacy null assembly rejection");
}

var real = Assembly.GetExecutingAssembly();
Loc.Setup("""{"Key":{"message":"Real"}}""", real);

var failures = 0;
Parallel.For(0, 10000, _ =>
{
    if (Loc.Localize("Key", "Fallback", real) != "Real") Interlocked.Increment(ref failures);
});
Check(failures == 0, "Concurrent warm reads");
var cold = typeof(object).Assembly;
Parallel.For(0, 10000, _ =>
{
    if (Loc.Localize("Key", "Fallback", cold) != "#Key") Interlocked.Increment(ref failures);
});
Check(failures == 0, "Concurrent first readers");

for (var i = 0; i < 1000; i++) _ = Loc.Localize("Key", "Fallback", real);
var allocated = GC.GetAllocatedBytesForCurrentThread();
for (var i = 0; i < 10000; i++)
    if (Loc.Localize("Key", "Fallback", real) != "Real") throw new Exception("Warm lookup changed");
allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
Check(allocated == 0, "Warm translated lookup allocated " + allocated + " bytes");

var weak = CreateCollectible();
for (var i = 0; i < 6; i++)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
}
Check(!weak.Context.IsAlive && !weak.Assembly.IsAlive, "Cache retained a collectible assembly");
Console.WriteLine($"{checks} checks passed; warm allocation {allocated} bytes; collectible context and assembly released.");

[MethodImpl(MethodImplOptions.NoInlining)]
static (WeakReference Context, WeakReference Assembly) CreateCollectible()
{
    var context = new AssemblyLoadContext("CacheChecks.Collectible", isCollectible: true);
    var assembly = context.LoadFromAssemblyPath(Assembly.GetExecutingAssembly().Location);
    Loc.SetupWithFallbacks(assembly);
    if (Loc.Localize("Key", "Collectible fallback", assembly) != "Collectible fallback") throw new Exception("Collectible lookup failed");
    var weak = (new WeakReference(context), new WeakReference(assembly));
    context.Unload();
    return weak;
}

sealed class CountingAssembly(string name) : Assembly
{
    private int nameReads;
    public string SimpleName { get; set; } = name;
    public int NameReads => Volatile.Read(ref this.nameReads);
    public override AssemblyName GetName() => this.GetName(false);
    public override AssemblyName GetName(bool copiedName)
    {
        Interlocked.Increment(ref this.nameReads);
        return new AssemblyName(this.SimpleName);
    }
}
