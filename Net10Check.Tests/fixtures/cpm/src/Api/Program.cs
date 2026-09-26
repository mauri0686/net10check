var host = new WebHostBuilder().Build();
builder.Services.Configure<ForwardedHeadersOptions>(o => o.KnownNetworks.Clear());
// o.KnownNetworks.Clear(); commented out, ignored