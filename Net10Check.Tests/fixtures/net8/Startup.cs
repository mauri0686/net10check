namespace App;
public class Startup
{
    // new WebHostBuilder() in a comment must not count
    public void Configure() => app.MapGet("/", () => "hi").WithOpenApi();
}