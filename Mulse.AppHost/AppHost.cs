var builder = DistributedApplication.CreateBuilder(args);

var service = builder.AddProject<Projects.Service>("service");

builder.AddProject<Projects.Mulse_Ui>("ui")
    .WithReference(service)
    .WaitFor(service);

builder.Build().Run();
