using DataIntelligence.Infrastructure;
using DataIntelligence.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddCollection(builder.Configuration);

//   (none)                          wait for the schedule and keep collecting
//   --once                          collect from every enabled source now, then exit
//   --backfill      [--from YEAR]   load both histories, then exit
//   --backfill-cpi  [--from YEAR]   load the CPI history only
//   --backfill-sofr                 load the SOFR history only
if (!WorkerCommandLine.TryParse(args, DateTime.UtcNow, out var runMode, out var error))
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine(WorkerCommandLine.Usage);
    return 1;
}

builder.Services.AddSingleton(runMode);
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();

return 0;
