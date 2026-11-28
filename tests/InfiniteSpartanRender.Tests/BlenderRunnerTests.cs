namespace InfiniteSpartanRender.Tests;

public class BlenderRunnerTests
{
    [Test]
    public void Blender_runs_the_script_in_the_background_with_the_loadout_and_output()
    {
        var args = BlenderRunner.Arguments("/app/blender/spartan.py", "/w/loadout.json", "/w/out.png");

        Assert.That(args, Is.EqualTo(new[]
        {
            "-b", "--factory-startup", "--python-use-system-env", "--python", "/app/blender/spartan.py", "--", "/w/loadout.json", "/w/out.png",
        }));
    }
}
