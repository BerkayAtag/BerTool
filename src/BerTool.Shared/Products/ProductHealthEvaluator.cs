using BerTool.Shared.Contracts;

namespace BerTool.Shared.Products;

/// <summary>
/// Ürün tanımı ile servis anlık görüntüsünü karşılaştırır.
/// Ajan ve sunucu aynı değerlendirme kodunu kullanır; böylece ürün
/// tanımı sunucudan değiştirildiğinde sonuçlar tutarlı kalır.
/// </summary>
public static class ProductHealthEvaluator
{
    public static ProductStatusDto Evaluate(
        ProductDefinitionDto product,
        IEnumerable<ServiceSnapshotDto> services)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(services);

        var expectedNames = product.ServiceNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (expectedNames.Count == 0)
        {
            return new ProductStatusDto
            {
                ProductId = product.Id,
                DisplayName = product.DisplayName,
                IsHealthy = false,
                StatusText = "Tanımsız",
                Details = "Ürün için izlenecek servis adı tanımlanmamış."
            };
        }

        var serviceList = services as IReadOnlyCollection<ServiceSnapshotDto> ?? services.ToList();
        var states = expectedNames
            .Select(name => new ServiceState(name, FindService(serviceList, name)))
            .ToList();

        if (product.MatchMode == ServiceMatchMode.Any)
        {
            var runningState = states.FirstOrDefault(state => state.Service is not null && IsRunning(state.Service));
            if (runningState is not null)
            {
                return Healthy(product, $"Çalışan servis: {runningState.Service!.Name}");
            }

            var foundStates = states.Where(state => state.Service is not null).ToList();
            if (foundStates.Count == 0)
            {
                return Missing(product, expectedNames);
            }

            return Unhealthy(
                product,
                "Çalışmıyor",
                $"Bulunan ancak çalışmayan servis(ler): {JoinDistinct(foundStates.Select(state => state.Service!.Name))}");
        }

        var missingNames = states
            .Where(state => state.Service is null)
            .Select(state => state.ExpectedName)
            .ToList();

        if (missingNames.Count > 0)
        {
            return Missing(product, missingNames);
        }

        var stoppedServices = states
            .Where(state => state.Service is not null && !IsRunning(state.Service))
            .Select(state => state.Service!.Name)
            .ToList();

        if (stoppedServices.Count > 0)
        {
            return Unhealthy(
                product,
                "Çalışmıyor",
                $"Çalışmayan servis(ler): {JoinDistinct(stoppedServices)}");
        }

        return Healthy(product, $"Çalışan servis(ler): {JoinDistinct(states.Select(state => state.Service!.Name))}");
    }

    public static List<ProductStatusDto> EvaluateAll(
        IEnumerable<ProductDefinitionDto> products,
        IEnumerable<ServiceSnapshotDto> services)
    {
        var serviceList = services.ToList();
        return products
            .Where(product => product.Enabled)
            .OrderBy(product => product.SortOrder)
            .ThenBy(product => product.DisplayName, StringComparer.CurrentCulture)
            .Select(product => Evaluate(product, serviceList))
            .ToList();
    }

    private static ProductStatusDto Healthy(ProductDefinitionDto product, string details)
    {
        return new ProductStatusDto
        {
            ProductId = product.Id,
            DisplayName = product.DisplayName,
            IsHealthy = true,
            StatusText = "Çalışıyor",
            Details = details
        };
    }

    private static ProductStatusDto Unhealthy(ProductDefinitionDto product, string statusText, string details)
    {
        return new ProductStatusDto
        {
            ProductId = product.Id,
            DisplayName = product.DisplayName,
            IsHealthy = false,
            StatusText = statusText,
            Details = details
        };
    }

    private static ProductStatusDto Missing(ProductDefinitionDto product, IEnumerable<string> missingNames)
    {
        return new ProductStatusDto
        {
            ProductId = product.Id,
            DisplayName = product.DisplayName,
            IsHealthy = false,
            StatusText = "Bulunamadı",
            Details = $"Bulunamayan servis(ler): {string.Join(", ", missingNames)}"
        };
    }

    // Farklı yazımlar (ör. "Zabbix Agent 2" ve "ZabbixAgent2") aynı servise
    // eşleşebilir; sonuç metninde servis bir kez gösterilir.
    private static string JoinDistinct(IEnumerable<string> names)
    {
        return string.Join(", ", names.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsRunning(ServiceSnapshotDto service)
    {
        return string.Equals(service.Status, "Running", StringComparison.OrdinalIgnoreCase);
    }

    private static ServiceSnapshotDto? FindService(
        IReadOnlyCollection<ServiceSnapshotDto> services,
        string expectedServiceName)
    {
        foreach (var service in services)
        {
            if (MatchesService(service, expectedServiceName))
            {
                return service;
            }
        }

        return null;
    }

    private static bool MatchesService(ServiceSnapshotDto service, string expectedServiceName)
    {
        if (string.Equals(service.Name, expectedServiceName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(service.DisplayName, expectedServiceName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var expectedNormalized = NormalizeServiceIdentifier(expectedServiceName);
        if (expectedNormalized.Length == 0)
        {
            return false;
        }

        var serviceNameNormalized = NormalizeServiceIdentifier(service.Name);
        var displayNameNormalized = NormalizeServiceIdentifier(service.DisplayName);

        if (serviceNameNormalized == expectedNormalized || displayNameNormalized == expectedNormalized)
        {
            return true;
        }

        // Kısa adlarda (örn. "DLP") yanlış eşleşmeyi önlemek için
        // yalnızca yeterince uzun adlarda içerme kontrolü yapılır.
        return expectedNormalized.Length >= 8 &&
            (serviceNameNormalized.Contains(expectedNormalized, StringComparison.OrdinalIgnoreCase) ||
             displayNameNormalized.Contains(expectedNormalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeServiceIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var characters = value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray();

        return new string(characters);
    }

    private sealed record ServiceState(string ExpectedName, ServiceSnapshotDto? Service);
}
