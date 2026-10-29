using BerTool.Shared.Contracts;
using BerTool.Shared.Products;
using BerTool.TrayAgent.Logging;
using BerTool.TrayAgent.Models;
using System.ServiceProcess;

namespace BerTool.TrayAgent.Services;

internal sealed class ServiceHealthChecker
{
    public ServiceHealthResult GetServiceStatuses(IReadOnlyList<ProductDefinitionDto> products)
    {
        var enabledProducts = (products ?? Array.Empty<ProductDefinitionDto>())
            .Where(product => product.Enabled)
            .ToList();

        if (enabledProducts.Count == 0)
        {
            return new ServiceHealthResult(Array.Empty<ServiceStatusItem>(), Array.Empty<ServiceSnapshotDto>());
        }

        ServiceController[] services;
        try
        {
            services = ServiceController.GetServices();
        }
        catch (Exception exception)
        {
            Logger.Error("Windows servis listesi alınamadı.", exception);
            var fallbackStatuses = enabledProducts
                .Select(product => new ServiceStatusItem
                {
                    ProductId = product.Id,
                    DisplayName = product.DisplayName,
                    StatusText = "Çalışmıyor",
                    IsHealthy = false,
                    Details = "Servis listesine erişilemedi."
                })
                .ToList();

            return new ServiceHealthResult(fallbackStatuses, Array.Empty<ServiceSnapshotDto>());
        }

        var snapshots = new List<ServiceSnapshotDto>(services.Length);
        try
        {
            foreach (var service in services)
            {
                try
                {
                    snapshots.Add(new ServiceSnapshotDto
                    {
                        Name = service.ServiceName,
                        DisplayName = service.DisplayName,
                        Status = service.Status.ToString()
                    });
                }
                catch (Exception exception)
                {
                    // Kaldırılmakta olan bir servis durum okumasında hata verebilir;
                    // diğer servislerin raporlanmasını engellememelidir.
                    Logger.Warn($"Servis durumu okunamadı: {exception.Message}");
                }
            }
        }
        finally
        {
            foreach (var service in services)
            {
                try
                {
                    service.Dispose();
                }
                catch
                {
                    // Servis nesnesi dispose edilemese de süreç devam eder.
                }
            }
        }

        var statuses = ProductHealthEvaluator
            .EvaluateAll(enabledProducts, snapshots)
            .Select(productStatus => new ServiceStatusItem
            {
                ProductId = productStatus.ProductId,
                DisplayName = productStatus.DisplayName,
                StatusText = productStatus.StatusText,
                IsHealthy = productStatus.IsHealthy,
                Details = productStatus.Details
            })
            .ToList();

        return new ServiceHealthResult(statuses, snapshots);
    }
}

internal sealed class ServiceHealthResult
{
    public ServiceHealthResult(
        IReadOnlyList<ServiceStatusItem> statuses,
        IReadOnlyList<ServiceSnapshotDto> services)
    {
        Statuses = statuses;
        Services = services;
    }

    public IReadOnlyList<ServiceStatusItem> Statuses { get; }
    public IReadOnlyList<ServiceSnapshotDto> Services { get; }
}
