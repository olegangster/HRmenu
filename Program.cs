using Amazon;
using Amazon.S3;
using TZApp.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var region = RegionEndpoint.GetBySystemName(cfg["Aws:Region"] ?? "eu-central-1");
    var accessKey = cfg["Aws:AccessKey"];
    var secretKey = cfg["Aws:SecretKey"];

    return string.IsNullOrEmpty(accessKey) || string.IsNullOrEmpty(secretKey)
        ? new AmazonS3Client(region)
        : new AmazonS3Client(accessKey, secretKey, region);
});
builder.Services.AddScoped<IResumeStorageService, ResumeStorageService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Menu}/{id?}");

app.Run();
