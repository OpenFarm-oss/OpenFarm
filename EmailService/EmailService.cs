using DatabaseAccess;
using RabbitMQHelper;

using EmailService.Interfaces;
using EmailService.Services;

using Serilog;
using Serilog.Templates;
using Serilog.Templates.Themes;
using Serilog.Formatting.Json;

/// Configure Serilog for wide event logging
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(new JsonFormatter(), "logs/log-.json", 
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7)
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    var conn = Environment.GetEnvironmentVariable("DATABASE_CONNECTION_STRING") 
            ?? throw new InvalidOperationException("DATABASE_CONNECTION_STRING is not set");

    builder.Services.AddSerilog();
    builder.Services.AddSingleton<Serilog.ILogger>(Log.Logger);
    builder.Services.AddSingleton<IRmqHelper, RmqHelper>();
    builder.Services.AddTransient<DatabaseAccessHelper>(_ => new DatabaseAccessHelper(conn));

    builder.Services.AddHostedService<EmailNotificationQueueWorker>();
    builder.Services.AddHostedService<IncomingEmailService>();
    builder.Services.AddSingleton<IEmailDeliveryService, EmailDeliveryService>();
    builder.Services.AddSingleton<IEmailTemplateService, EmailTemplateService>();
    builder.Services.AddSingleton<IEmailAutoReplyService, EmailAutoReplyService>();

    var host = builder.Build();
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, $"EmailService terminated unexpectedly with error: {ex.Message}");
}
finally
{
    Log.CloseAndFlush();
}