using Build.Modules;
using Build.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModularPipelines;
using ModularPipelines.Extensions;

var builder = Pipeline.CreateBuilder();

builder.Configuration.AddJsonFile("appsettings.json");
builder.Configuration.AddUserSecrets<Program>();
builder.Configuration.AddEnvironmentVariables();

builder.Services.Configure<BuildOptions>(builder.Configuration.GetSection("Build"));
builder.Services.Configure<PackOptions>(builder.Configuration.GetSection("Pack"));
builder.Services.Configure<NuGetOptions>(builder.Configuration.GetSection("NuGet"));
builder.Services.Configure<PublishOptions>(builder.Configuration.GetSection("Publish"));
builder.Services.Configure<TrackOptions>(builder.Configuration.GetSection("Track"));
builder.Services.Configure<UpdateOptions>(builder.Configuration.GetSection("Update"));

if (args.Contains("clean-nuget"))
{
    builder.Services.AddModule<DeleteNugetModule>();
}

if (args.Contains("pack"))
{
    builder.Services.AddModule<CleanProjectModule>();
    builder.Services.AddModule<PackProjectModule>();
    builder.Services.AddModule<UpdateReadmeModule>();
    builder.Services.AddModule<RestoreReadmeModule>();
}

if (args.Contains("publish"))
{
    builder.Services.AddModule<PublishNugetModule>();
    builder.Services.AddModule<PublishGithubModule>();
}

if (args.Contains("track"))
{
    builder.Services.AddModule<TrackUpdatesModule>();
}

if (args.Contains("update"))
{
    builder.Services.AddModule<ExtractUpdateModule>();
    builder.Services.AddModule<PublishUpdateModule>();
}

await builder.Build().RunAsync();
