using TestGen.Cli;
using TestGen.Mock;

namespace TestGen.Tests;

/// <summary>testgen mock: användningsfel och raderna på stdout. Själva mocken testas i TestGen.Mock.Tests.</summary>
public class MockCommandTests
{
    private static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var (o, e) = (Console.Out, Console.Error);
        var (so, se) = (new StringWriter { NewLine = "\n" }, new StringWriter { NewLine = "\n" });
        Console.SetOut(so); Console.SetError(se);
        try { return (Program.Main(args), so.ToString(), se.ToString()); }
        finally { Console.SetOut(o); Console.SetError(e); }
    }

    [Test]
    public void Without_a_spec_it_is_a_usage_error()
    {
        var (exit, o, e) = Cli("mock");
        Assert.That((exit, o), Is.EqualTo((2, "")));
        Assert.That(e, Does.Contain("Hittar ingen spec att starta en mock för").And.Contain("testgen mock --spec"));
    }

    [Test]
    public void Port_with_two_specs_is_a_usage_error()
    {
        var (exit, _, e) = Cli("mock", "--spec", ShopSpec.Path, "--spec", ShopSpec.Path, "--port", "5080");
        Assert.That(exit, Is.EqualTo(2));
        Assert.That(e, Does.Contain("--port går bara att använda med en spec"));
    }

    [TestCase("abc")]
    [TestCase("0")]
    [TestCase("70000")]
    public void A_port_outside_the_range_is_a_usage_error(string port)
    {
        var (exit, _, e) = Cli("mock", "--spec", ShopSpec.Path, "--port", port);
        Assert.That(exit, Is.EqualTo(2));
        Assert.That(e, Does.Contain("--port måste vara ett tal mellan 1 och 65535"));
    }

    [Test]
    public void A_missing_spec_file_is_reported_as_a_missing_source()
    {
        var (exit, _, e) = Cli("mock", "--spec", "finns-inte.yaml");
        Assert.That(exit, Is.Not.EqualTo(0));
        Assert.That(e, Does.Contain("finns-inte.yaml"));
    }

    [Test]
    public void A_logged_call_is_one_line_with_fixed_columns()
    {
        MockCall Call(string method, string path, string? op, int status, params string[] problems) =>
            new(0, method, path, "", null, op, status, problems);

        Assert.That(MockCommand.Format(Call("POST", "/products", "createProduct", 201)), Is.EqualTo("POST /products               201  createProduct"));
        Assert.That(MockCommand.Format(Call("POST", "/products", "createProduct", 400, "name: saknas (obligatoriskt enligt specen)", "price: saknas")),
            Is.EqualTo("POST /products               400  name: saknas (obligatoriskt enligt specen) (+1)"));
        Assert.That(MockCommand.Format(Call("GET", "/nothing", null, 404, "Specen har ingen operation")), Does.Contain(" 404  Specen har"));
        Assert.That(MockCommand.Format(Call("GET", "/orders/1", "getOrder", 200), "Shop  "), Is.EqualTo("Shop  GET /orders/1                200  getOrder"));
    }
}
