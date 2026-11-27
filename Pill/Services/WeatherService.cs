using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Devices.Geolocation;
using Pill.Models;

namespace Pill.Services;

public sealed class WeatherService : ObservableObject, IDisposable
{
    private const string ForecastEndpoint = "https://api.open-meteo.com/v1/forecast";
    private const string GeocodeEndpoint = "https://geocoding-api.open-meteo.com/v1/search";
    private const string IpLocationEndpoint = "https://ipapi.co/json/";
    private const string GeoJsEndpoint = "https://get.geojs.io/v1/ip/geo.json";

    private readonly DispatcherTimer _timer;
    private readonly HttpClient _client;
    private bool _disposed;
    private bool _refreshing;
    private string _temperature = "--°C";
    private string _condition = "NO DATA";
    private string _icon = "○";
    private string _text = "--°C NO DATA";
    private string _detail = "NO DATA";
    private string _feelsLike = "--°C";
    private string _locationStatus = "Auto";
    private string _locationName = "Detecting location";

    public string Temperature { get => _temperature; private set => Set(ref _temperature, value); }
    public string Condition { get => _condition; private set => Set(ref _condition, value); }
    public string Icon { get => _icon; private set => Set(ref _icon, value); }
    public string Text { get => _text; private set => Set(ref _text, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public string FeelsLike { get => _feelsLike; private set => Set(ref _feelsLike, value); }
    public string LocationStatus { get => _locationStatus; private set => Set(ref _locationStatus, value); }
    public string LocationName { get => _locationName; private set => Set(ref _locationName, value); }

    public WeatherService()
    {
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Pill/1.0");
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _timer.Tick += (_, _) => Reload();
    }

    public void Start()
    {
        if (_disposed) return;
        Reload();
        _timer.Start();
    }

    public void Reload() => _ = RefreshAsync();

    public async Task<bool> RequestDeviceLocationAsync()
    {
        Location? location = await ReadDeviceLocationAsync();
        if (location is null) return false;

        SaveCachedLocation(location, LocationName);
        Reload();
        return true;
    }

    private async Task RefreshAsync()
    {
        if (_disposed || _refreshing) return;
        _refreshing = true;
        try
        {
            AppSettings settings = SettingsService.Load();
            WeatherConfig config = LoadConfig();
            Location? location = await ResolveLocationAsync(settings);
            if (location is null) return;

            string endpoint = string.IsNullOrWhiteSpace(config.Endpoint) ? ForecastEndpoint : config.Endpoint;
            string unit = settings.WeatherUnit == "F" ? "fahrenheit" : "celsius";
            string suffix = settings.WeatherUnit == "F" ? "°F" : "°C";

            string url = endpoint +
                "?latitude=" + location.Latitude.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) +
                "&longitude=" + location.Longitude.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) +
                "&current=temperature_2m,apparent_temperature,weather_code" +
                "&temperature_unit=" + unit +
                "&timezone=auto";

            if (!string.IsNullOrWhiteSpace(config.ApiKey))
                url += "&apikey=" + Uri.EscapeDataString(config.ApiKey.Trim());

            string json = await _client.GetStringAsync(url);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement current = doc.RootElement.GetProperty("current");
            double value = current.GetProperty("temperature_2m").GetDouble();
            double apparent = value;
            if (current.TryGetProperty("apparent_temperature", out JsonElement feels))
                apparent = feels.GetDouble();
            int code = current.GetProperty("weather_code").GetInt32();

            string format = "0";
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            Temperature = Math.Round(value, 0).ToString(format, culture) + suffix;
            FeelsLike = Math.Round(apparent, 0).ToString(format, culture) + suffix;
            Condition = Describe(code);
            Detail = Condition + " · FEELS " + FeelsLike;
            Icon = IconFor(code);
            Text = Temperature + " " + Condition;
        }
        catch
        {
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task<Location?> ResolveLocationAsync(AppSettings settings)
    {
        WeatherConfig config = LoadConfig();
        if (config.Latitude.HasValue && config.Longitude.HasValue)
        {
            LocationStatus = "Configured";
            LocationName = "Custom coordinates";
            return new Location(config.Latitude.Value, config.Longitude.Value);
        }

        if (settings.UseDeviceLocation)
        {
            Location? device = await ReadDeviceLocationAsync();
            if (device is not null)
            {
                SaveCachedLocation(device, LocationName);
                return device;
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.WeatherCity))
        {
            string geocodeUrl = GeocodeEndpoint +
                "?name=" + Uri.EscapeDataString(settings.WeatherCity.Trim()) +
                "&count=1&language=en&format=json";
            if (!string.IsNullOrWhiteSpace(settings.WeatherCountry))
                geocodeUrl += "&country=" + Uri.EscapeDataString(settings.WeatherCountry.Trim());

            try
            {
                string json = await _client.GetStringAsync(geocodeUrl);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("results", out JsonElement results) &&
                    results.GetArrayLength() > 0)
                {
                    JsonElement first = results[0];
                    var location = new Location(
                        first.GetProperty("latitude").GetDouble(),
                        first.GetProperty("longitude").GetDouble());
                    LocationStatus = "City";
                    LocationName = ReadName(first, settings.WeatherCity);
                    SaveCachedLocation(location, LocationName);
                    return location;
                }
                LocationStatus = "City not found";
                LocationName = settings.WeatherCity.Trim();
            }
            catch
            {
                LocationStatus = "City failed";
            }
        }

        Location? cached = LoadCachedLocation();
        if (cached is not null)
        {
            LocationStatus = "Saved";
            LocationName = LoadCachedName();
            return cached;
        }

        foreach (string endpoint in new[] { IpLocationEndpoint, GeoJsEndpoint })
        {
            try
            {
                string json = await _client.GetStringAsync(endpoint);
                using JsonDocument doc = JsonDocument.Parse(json);
                var location = new Location(
                    ReadCoordinate(doc.RootElement, "latitude"),
                    ReadCoordinate(doc.RootElement, "longitude"));
                LocationStatus = "Detected";
                LocationName = ReadName(doc.RootElement, "Detected location");
                SaveCachedLocation(location, LocationName);
                return location;
            }
            catch
            {
            }
        }

        LocationStatus = "Unavailable";
        return null;
    }

    private async Task<Location?> ReadDeviceLocationAsync()
    {
        try
        {
            GeolocationAccessStatus access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed)
            {
                LocationStatus = "Permission denied";
                return null;
            }

            var locator = new Geolocator
            {
                DesiredAccuracy = PositionAccuracy.Default,
                MovementThreshold = 10
            };
            Geoposition position = await locator.GetGeopositionAsync(
                TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10));
            BasicGeoposition point = position.Coordinate.Point.Position;
            LocationStatus = "Device";
            LocationName = "Device location";
            return new Location(point.Latitude, point.Longitude);
        }
        catch
        {
            LocationStatus = "Location failed";
            return null;
        }
    }

    private static double ReadCoordinate(JsonElement root, string name)
    {
        JsonElement value = root.GetProperty(name);
        if (value.ValueKind == JsonValueKind.String)
            return double.Parse(value.GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        return value.GetDouble();
    }

    private static string ReadName(JsonElement root, string fallback)
    {
        string city = ReadText(root, "city") ?? ReadText(root, "name") ?? "";
        if (string.IsNullOrWhiteSpace(city)) return fallback;
        string country = ReadText(root, "country") ?? ReadText(root, "country_code") ?? "";
        return string.IsNullOrWhiteSpace(country) || string.Equals(country, city, StringComparison.OrdinalIgnoreCase)
            ? city.Trim()
            : city.Trim() + ", " + country.Trim();
    }

    private static string? ReadText(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value)) return null;
        if (value.ValueKind != JsonValueKind.String) return null;
        string text = value.GetString() ?? "";
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static WeatherConfig LoadConfig()
    {
        foreach (string path in ConfigPaths())
        {
            try
            {
                if (!File.Exists(path)) continue;
                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<WeatherConfig>(json) ?? new WeatherConfig();
            }
            catch
            {
            }
        }
        return new WeatherConfig();
    }

    private static string[] ConfigPaths()
    {
        string data = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pill",
            "weather.json");

        return new[]
        {
            Path.Combine(AppContext.BaseDirectory, "weather.local.json"),
            Path.Combine(Environment.CurrentDirectory, "weather.local.json"),
            data
        };
    }

    private static Location? LoadCachedLocation()
    {
        try
        {
            string path = CachePath();
            if (!File.Exists(path)) return null;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            return new Location(
                doc.RootElement.GetProperty("latitude").GetDouble(),
                doc.RootElement.GetProperty("longitude").GetDouble());
        }
        catch
        {
            return null;
        }
    }

    private static string LoadCachedName()
    {
        try
        {
            string path = CachePath();
            if (!File.Exists(path)) return "Saved location";
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("name", out JsonElement value) &&
                value.ValueKind == JsonValueKind.String)
            {
                string name = value.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
        }
        catch
        {
        }
        return "Saved location";
    }

    private static void SaveCachedLocation(Location location, string name = "Saved location")
    {
        try
        {
            string path = CachePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                latitude = location.Latitude,
                longitude = location.Longitude,
                name
            }));
        }
        catch
        {
        }
    }

    private static string CachePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pill",
        "weather-location.json");

    private static string Describe(int code) => code switch
    {
        0 => "CLEAR",
        1 => "SUNNY",
        2 => "PARTLY",
        3 => "CLOUDY",
        45 or 48 => "FOG",
        >= 51 and <= 57 => "DRIZZLE",
        >= 61 and <= 67 => "RAIN",
        >= 71 and <= 77 => "SNOW",
        >= 80 and <= 82 => "SHOWERS",
        >= 85 and <= 86 => "SNOW",
        >= 95 => "STORM",
        _ => "WEATHER"
    };

    private static string IconFor(int code) => code switch
    {
        0 or 1 => "☀",
        2 or 3 => "☁",
        45 or 48 => "≡",
        >= 51 and <= 57 => "☂",
        >= 61 and <= 67 => "☂",
        >= 71 and <= 77 => "❄",
        >= 80 and <= 82 => "☂",
        >= 85 and <= 86 => "❄",
        >= 95 => "⚡",
        _ => "○"
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _client.Dispose();
    }

    private sealed record Location(double Latitude, double Longitude);

    private sealed class WeatherConfig
    {
        public string? Endpoint { get; set; }
        public string? ApiKey { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
    }
}
