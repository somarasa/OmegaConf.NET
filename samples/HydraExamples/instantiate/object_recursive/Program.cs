// Ported from Hydra: instantiate/object_recursive
// Lesson: instantiate() recurses into nested _target_ objects (singletons and lists).
using OmegaConf.NET;
using OmegaConf.NET.Serialization;
using OmegaConf.NET.Composition;

namespace my_app;

public class Driver
{
    public string? Name { get; set; }
    public int Age { get; set; }
}

public class Wheel
{
    public int Radius { get; set; }
    public int Width { get; set; }
}

public class Car
{
    public Driver? Driver { get; set; }
    public List<Wheel>? Wheels { get; set; }
    public void Drive() => Console.WriteLine($"Driver : {Driver?.Name}, {Wheels?.Count} wheels");
}

public static class Entry
{
    public static int Main(string[] args)
    {
        var node = OmegaConfBuilder.Create()
            .SetRootDirectory(AppContext.BaseDirectory)
            .AddCliArgs(args)
            .BuildNode();
        var car = OmegaConfig.Instantiate<Car>(node.SelectNode("car")!, allowedTargets: ["my_app.*"]);
        car.Drive();
        return 0;
    }
}
