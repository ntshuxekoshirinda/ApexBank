using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class CustomWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime {
    private SqliteConnection _connection = null!;

    public async Task InitializeAsync() {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) {
        // Force the test host environment to "Testing" so Program.cs skips Npgsql
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services => {
            // Register SQLite in-memory exclusively for tests
            services.AddDbContext<AppDbContext>(options => {
                options.UseSqlite(_connection);
            });

            // Automatically build the database schema in RAM
            var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
        });
    }

    public new async Task DisposeAsync() {
        if (_connection != null) {
            await _connection.DisposeAsync();
        }
        await base.DisposeAsync();
    }
}