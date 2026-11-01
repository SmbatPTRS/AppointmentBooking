FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/AppointmentBooking.Domain/AppointmentBooking.Domain.csproj src/AppointmentBooking.Domain/
COPY src/AppointmentBooking.Application/AppointmentBooking.Application.csproj src/AppointmentBooking.Application/
COPY src/AppointmentBooking.Infrastructure/AppointmentBooking.Infrastructure.csproj src/AppointmentBooking.Infrastructure/
COPY src/AppointmentBooking.Api/AppointmentBooking.Api.csproj src/AppointmentBooking.Api/
RUN dotnet restore src/AppointmentBooking.Api/AppointmentBooking.Api.csproj
COPY src/ src/
RUN dotnet publish src/AppointmentBooking.Api/AppointmentBooking.Api.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish ./
USER app
ENTRYPOINT ["dotnet", "AppointmentBooking.Api.dll"]
