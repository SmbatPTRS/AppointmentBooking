using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Application.Identity;
using AppointmentBooking.Infrastructure.Persistence;
using AppointmentBooking.Infrastructure.Repositories;
using AppointmentBooking.Infrastructure.Time;
using AppointmentBooking.Infrastructure.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AppointmentBooking.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("DefaultConnection is not configured.");

        services.AddDbContext<AppDbContext>(
            options => options.UseNpgsql(connectionString));

        services.AddScoped<IUnitOfWork>(provider =>
            provider.GetRequiredService<AppDbContext>());

        services.AddScoped<IScheduleRepository, ScheduleRepository>();
        services.AddScoped<ICatalogRepository, CatalogRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<UserService>();
        services.AddScoped<IBookingRepository, BookingRepository>();
        services.AddScoped<IBookingCoordinationRepository, BookingCoordinationRepository>();
        services.AddScoped<DatabaseSeeder>();

        services.AddScoped<IStaffOperationLock, PostgresStaffOperationLock>();
        services.AddSingleton<IClock, SystemClock>();

        return services;
    }
}