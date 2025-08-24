// nuuz.api/Configuration/ServiceCollectionExtensions.cs
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Google.Cloud.Firestore.V1;
using Google.Cloud.Storage.V1;
using Grpc.Auth;
using Grpc.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nuuz.Application.Abstraction;
using Nuuz.Application.Services;
using Nuuz.Infrastructure.Repositories;
using Nuuz.Infrastructure.Services;

namespace nuuz.api.Configuration;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Firebase Admin, Firestore, GCS and Nuuz services (including feedback + GCS screenshot storage).
    /// Uses appsettings.json for storage settings (with env-var fallback) and your existing dev/prod credential pattern.
    /// </summary>
    public static IServiceCollection AddFirebaseServices(
        this IServiceCollection services,
        IWebHostEnvironment environment,
        IConfiguration configuration)
    {
        // -------- Firebase project & credentials --------
        var projectId = "terratutor-cd20e";

        GoogleCredential googleCredential;
        FirebaseApp firebaseApp;

        if (environment.IsDevelopment())
        {
            // DEV: local service account file
            var filepath = @"D:\development\firebasekey\terratutor-cd20e-firebase-adminsdk-dmaat-007c77a1a9.json";
            googleCredential = GoogleCredential.FromFile(filepath);
        }
        else
        {
            // PROD: credentials from env var JSON payload
            var credentialsJson = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS_JSON");
            if (string.IsNullOrWhiteSpace(credentialsJson))
                throw new InvalidOperationException("Firebase credentials must be provided in the GOOGLE_APPLICATION_CREDENTIALS_JSON environment variable.");
            googleCredential = GoogleCredential.FromJson(credentialsJson);
        }

        if (FirebaseApp.DefaultInstance == null)
        {
            firebaseApp = FirebaseApp.Create(new AppOptions
            {
                Credential = googleCredential,
                ProjectId = projectId,
            });
        }
        else
        {
            firebaseApp = FirebaseApp.DefaultInstance;
        }

        services.AddSingleton(firebaseApp);

        // -------- Firestore (explicit client using the same credential) --------
        ChannelCredentials grpcCredentials = googleCredential.ToChannelCredentials();
        FirestoreClient firestoreClient = new FirestoreClientBuilder
        {
            ChannelCredentials = grpcCredentials,
        }.Build();

        var firestoreDb = FirestoreDb.Create(projectId, client: firestoreClient);
        services.AddSingleton(firestoreDb);

        // -------- Google Cloud Storage --------
        services.AddSingleton<StorageClient>(_ => StorageClient.Create(googleCredential));

        // -------- Screenshot storage config (appsettings first, env fallback) --------
        // appsettings.json:
        //   "Storage": { "ScreenshotBucket": "<bucket>", "MakePublic": true }
        // env fallback:
        //   NUUZ_SCREENSHOT_BUCKET, NUUZ_SCREENSHOT_PUBLIC=true|false
        var screenshotBucket =
            configuration["Storage:ScreenshotBucket"] ??
            Environment.GetEnvironmentVariable("NUUZ_SCREENSHOT_BUCKET");

        bool makePublic = false;
        var makePublicFromConfig = configuration["Storage:MakePublic"];
        if (!string.IsNullOrWhiteSpace(makePublicFromConfig) &&
            bool.TryParse(makePublicFromConfig, out var parsed))
        {
            makePublic = parsed;
        }
        else
        {
            var makePublicEnv = Environment.GetEnvironmentVariable("NUUZ_SCREENSHOT_PUBLIC");
            if (!string.IsNullOrWhiteSpace(makePublicEnv))
                bool.TryParse(makePublicEnv, out makePublic);
        }

        if (!string.IsNullOrWhiteSpace(screenshotBucket))
        {
            services.AddSingleton<IScreenshotStorage>(sp =>
                new GcsScreenshotStorage(
                    sp.GetRequiredService<StorageClient>(),
                    screenshotBucket!,
                    makePublic: makePublic));
        }
        else
        {
            // No bucket configured → feedback service will skip uploading screenshots (still stores metadata).
            services.AddSingleton<IScreenshotStorage?>(sp => null);
        }

        // -------- Generic base repository (yours) --------
        services.AddScoped(typeof(IBaseRepository<>), typeof(BaseRepository<>));

        // -------- Existing repositories/services --------
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IInterestRepository, InterestRepository>();
        services.AddScoped<INewsArticleRepository, NewsArticleRepository>();
        services.AddScoped<IArticleSummaryRepository, ArticleSummaryRepository>();
        services.AddScoped<IArticleRepository, ArticleRepository>();
        services.AddScoped<IUserSaveRepository, UserSaveRepository>();

        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IInterestService, InterestService>();
        services.AddScoped<IFeedService, FeedService>();

        // -------- NEW: Feedback repo/service (Firestore + optional GCS) --------
        services.AddScoped<IFeedbackRepository, FirestoreFeedbackRepository>();
        services.AddScoped<IFeedbackService, FeedbackService>();

        // Seed helpers (DEV only)
        if (environment.IsDevelopment())
        {
            services.AddHostedService<Nuuz.Api.Seeding.SeedInterestsHostedService>();
        }

        return services;
    }
}
