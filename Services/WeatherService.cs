using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Geolocation;

namespace Pebble
{
    sealed class WeatherInfo
    {
        public double Temp, Hi, Lo;
        public int Code, Day;
        public string City;
    }

    /// <summary>Current weather from Open-Meteo (free, no API key). Location from Windows, or a city the user typed.</summary>
    sealed class WeatherService
    {
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        readonly SemaphoreSlim _wake = new SemaphoreSlim(0);

        public volatile WeatherInfo Current;
        public volatile string Error = "";
        public int Ver;
        public volatile string City = "";
        public volatile bool Fahrenheit = !RegionInfo.CurrentRegion.IsMetric;

        public static bool RegionUsesFahrenheit => !RegionInfo.CurrentRegion.IsMetric;

        public void Start() => Task.Run(Loop);

        public void SetCity(string city)
        {
            City = city ?? "";
            Current = null; Error = "";
            Interlocked.Increment(ref Ver);
            _wake.Release();
        }

        async Task Loop()
        {
            while (true)
            {
                TimeSpan next;
                try { next = await FetchOnce(); }
                catch (Exception e)
                {
                    Log.Write("weather: " + e.Message);
                    Error = "Weather unavailable"; Interlocked.Increment(ref Ver);
                    next = TimeSpan.FromMinutes(3);
                }
                await _wake.WaitAsync(next);
                while (_wake.CurrentCount > 0) _wake.Wait(0);
            }
        }

        async Task<TimeSpan> FetchOnce()
        {
            double? lat = null, lon = null;
            string name = "";
            string city = City;

            if (!string.IsNullOrWhiteSpace(city))
            {
                string json = await Http.GetStringAsync("https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&name=" + Uri.EscapeDataString(city));
                using var g = JsonDocument.Parse(json);
                if (g.RootElement.TryGetProperty("results", out var res) && res.GetArrayLength() > 0)
                {
                    var r = res[0];
                    lat = r.GetProperty("latitude").GetDouble();
                    lon = r.GetProperty("longitude").GetDouble();
                    name = r.GetProperty("name").GetString();
                }
                else
                {
                    Current = null; Error = "Couldn't find " + city; Interlocked.Increment(ref Ver);
                    return TimeSpan.FromMinutes(15);
                }
            }
            else
            {
                try
                {
                    if (await Geolocator.RequestAccessAsync() == GeolocationAccessStatus.Allowed)
                    {
                        var pos = await new Geolocator().GetGeopositionAsync(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(15));
                        lat = pos.Coordinate.Point.Position.Latitude;
                        lon = pos.Coordinate.Point.Position.Longitude;
                        name = "Here";
                    }
                }
                catch (Exception e) { Log.Write("location: " + e.Message); }
            }

            if (lat == null)
            {
                Error = "NoLocation"; Interlocked.Increment(ref Ver);
                return TimeSpan.FromMinutes(30);
            }

            string url = string.Format(CultureInfo.InvariantCulture,
                "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}&current=temperature_2m,weather_code,is_day" +
                "&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1{2}",
                lat, lon, Fahrenheit ? "&temperature_unit=fahrenheit" : "");
            using var f = JsonDocument.Parse(await Http.GetStringAsync(url));
            var cur = f.RootElement.GetProperty("current");
            var daily = f.RootElement.GetProperty("daily");
            Current = new WeatherInfo
            {
                Temp = cur.GetProperty("temperature_2m").GetDouble(),
                Code = cur.GetProperty("weather_code").GetInt32(),
                Day = cur.GetProperty("is_day").GetInt32(),
                Hi = daily.GetProperty("temperature_2m_max")[0].GetDouble(),
                Lo = daily.GetProperty("temperature_2m_min")[0].GetDouble(),
                City = name
            };
            Error = ""; Interlocked.Increment(ref Ver);
            return TimeSpan.FromMinutes(15);
        }
    }
}
