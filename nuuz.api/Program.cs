using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.OpenApi.Models;
using Microsoft.IdentityModel.Tokens;
using nuuz.api.Configuration;
using Nuuz.Application.Abstraction;
using Nuuz.Application.Services;
using Nuuz.Infrastructure.Repositories;
using Nuuz.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

// ------------------------- CORS -------------------------
builder.Services.AddCors(options =>
{
    options.AddPolicy("DevTunnels", policy =>
    {
        policy
            .SetIsOriginAllowed(origin =>
            {
                if (string.IsNullOrWhiteSpace(origin)) return false;
                try
                {
                    var host = new Uri(origin).Host.ToLowerInvariant();
                    return host == "localhost"
                        || host == "127.0.0.1"
                        || host.EndsWith(".ngrok-free.app")
                        || host.EndsWith(".trycloudflare.com")
                        || host.EndsWith(".loca.lt")
                        || host.EndsWith(".devtunnels.ms");
                }
                catch
                {
                    return false;
                }
            })
            .AllowAnyHeader()
            .AllowAnyMethod();
        // .AllowCredentials(); // only if you use cookies in dev
    });
});

// ------------------ MVC / Swagger -----------------------
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "My API", Version = "v1" });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter 'Bearer {token}'"
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } },
            Array.Empty<string>()
        }
    });
});

// ------------- Firebase / Firestore helpers -------------
builder.Services.AddFirebaseServices(builder.Environment, builder.Configuration);

// --------------------- Repositories ---------------------
builder.Services.AddScoped<IArticleRepository, ArticleRepository>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IUserSaveRepository, UserSaveRepository>();
builder.Services.AddScoped<IInterestRepository, InterestRepository>();
builder.Services.AddScoped<IUserMoodRepository, UserMoodRepository>();
builder.Services.AddSingleton<IPulseSnapshotRepository, Nuuz.Infrastructure.Repositories.PulseSnapshotRepository>();
builder.Services.AddScoped<IConnectedAccountRepository, ConnectedAccountRepository>();
builder.Services.AddScoped<IShareLinkRepository, ShareLinkRepository>();
builder.Services.AddScoped<IShareEventRepository, ShareEventRepository>();
builder.Services.AddScoped<IOAuthStateRepository, OAuthStateRepository>();
builder.Services.AddScoped<IFeedbackRepository, FirestoreFeedbackRepository>();

// ------------------ Domain Services ---------------------
builder.Services.AddScoped<IMoodService, MoodService>();
builder.Services.AddScoped<IInterestMatcher, InterestMatcher>();
// IMPORTANT: Scoped (not singleton) — it depends on scoped IArticleRepository
builder.Services.AddScoped<IMoodFeedbackService, MoodFeedbackService>();
builder.Services.AddScoped<IMoodModelService, MoodModelService>();
builder.Services.AddScoped<IPulseService, PulseService>();
builder.Services.AddScoped<IShareService, ShareService>();
builder.Services.AddScoped<IShareProvider, TwitterShareProvider>();
builder.Services.AddScoped<IShareProvider, BlueskyShareProvider>();
builder.Services.AddScoped<IFeedbackService, FeedbackService>();

// ✅ Register the feed service implementation
builder.Services.AddScoped<IFeedService, FeedService>();

// --------------- HttpClient factory ---------------------
builder.Services.AddHttpClient(); // CreateClient()
builder.Services.AddHttpClient<IContentExtractor, SimpleContentExtractor>();

var useLocalSummarizer = builder.Configuration.GetValue<bool>("UseLocalSummarizer");
if (useLocalSummarizer)
{
    builder.Services.AddSingleton<IAISummarizer, OnnxSummarizer>();
}
else
{
    builder.Services.AddHttpClient<IAISummarizer, OpenAiSummarizer>();
}

builder.Services.AddHttpClient<IPulseReranker, PulseRerankerOpenAI>();

// Optional named client
builder.Services.AddHttpClient(nameof(EmbedProbeService));

// Helpers
builder.Services.AddScoped<IEmbedProbeService, EmbedProbeService>();

// ---- OpenAI LLM wiring ----
builder.Services.AddHttpClient("openai", client =>
{
    client.BaseAddress = new Uri("https://api.openai.com/v1/");
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
});
builder.Services.AddSingleton<ILLMClient>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var apiKey = cfg["OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing OpenAI:ApiKey");
    var model = cfg["OpenAI:Model"] ?? "gpt-4o-mini";

    var http = factory.CreateClient("openai");
    return new OpenAIChatClient(http, apiKey, model);
});

// SparkNotes generator
builder.Services.AddSingleton<ISparkNotesService, SparkNotesService>();

// Unified (one-call) summarizer + SparkNotes
builder.Services.AddHttpClient<IUnifiedNotesService, UnifiedNotesService>();

// ------------------ Background workers ------------------
builder.Services.AddHostedService<PulseSnapshotService>();
builder.Services.AddHostedService<MoodModelTrainingService>();
var ingestionEnabled = builder.Configuration.GetValue<bool>("Ingestion:Enabled");
if (builder.Environment.IsDevelopment() && ingestionEnabled)
{
    builder.Services.AddHostedService<NewsIngestionService>();
}

// --------------------- JWT Auth -------------------------
builder.Services.AddAuthentication()
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        var jwtSettings = builder.Configuration.GetSection("JwtSettings");
        options.Authority = jwtSettings["Issuer"];
        options.Audience = jwtSettings["Audience"];
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSettings["Issuer"],
        };
    });

var app = builder.Build();

// --------------- Dev helpers: Swagger -------------------
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// --------- Pipeline order (CORS before static) ----------
app.UseHttpsRedirection();
app.UseCors("DevTunnels");
app.UseAuthentication();
app.UseAuthorization();
app.UseStaticFiles();

// ------------------ MVC endpoints -----------------------
app.MapControllers();

// --------- Explicit endpoint for client-metadata --------
app.MapGet("/oauth/client-metadata.json", (IWebHostEnvironment env) =>
{
    var root = env.WebRootPath; // <project>/wwwroot
    var path = Path.Combine(root ?? "", "oauth", "client-metadata.json");
    if (!System.IO.File.Exists(path))
        return Results.NotFound(new { error = "metadata not found", lookedAt = path });

    return Results.File(path, "application/json; charset=utf-8");
}).AllowAnonymous();

// Little debug helper
app.MapGet("/_debug/webroot", (IWebHostEnvironment env) =>
{
    return Results.Json(new
    {
        env.WebRootPath,
        exists = System.IO.Directory.Exists(env.WebRootPath ?? "")
    });
}).AllowAnonymous();

app.Run();
