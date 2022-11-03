﻿using System.Collections.Concurrent;
using Dan.Common.Interfaces;

namespace Dan.Common.Services;
public class EntityRegistryService : IEntityRegistryService
{
    private readonly IEntityRegistryApiClientService _entityRegistryApiClientService;
    public bool UseCoreProxy { get; set; } = true;
    public bool AllowTestCcrLookup { get; set; } = false;

    public const string CcrProxyMainUnitDatasetName = "_ccrproxymain";
    public const string CcrProxySubUnitDatasetName  = "_ccrproxysub";

    private const string MainUnitLookupEndpoint         = "http://data.brreg.no/enhetsregisteret/api/enheter/{0}";
    private const string SubUnitLookupEndpoint          = "http://data.brreg.no/enhetsregisteret/api/underenheter/{0}";
    private const string PpeMainUnitLookupEndpoint      = "http://data.ppe.brreg.no/enhetsregisteret/api/enheter/{0}";
    private const string PpeSubUnitLookupEndpoint       = "http://data.ppe.brreg.no/enhetsregisteret/api/underenheter/{0}";
    
    private const string ProxyMainUnitLookupEndpoint    = "https://api.data.altinn.no/api/opendata/" + CcrProxyMainUnitDatasetName + "/{0}";
    private const string ProxySubUnitLookupEndpoint     = "https://api.data.altinn.no/api/opendata/" + CcrProxySubUnitDatasetName + "/{0}";
    private const string PpeProxyMainUnitLookupEndpoint = "https://api.data.altinn.no/api/opendata/" + CcrProxyMainUnitDatasetName + "/{0}";
    private const string PpeProxySubUnitLookupEndpoint  = "https://api.data.altinn.no/api/opendata/" + CcrProxySubUnitDatasetName + "/{0}";

    private static readonly string[] PublicSectorUnitTypes   = { "ADOS", "FKF", "FYLK", "KF", "KOMM", "ORGL", "STAT", "SF", "SÆR" };
    private static readonly string[] PublicSectorSectorCodes = { "1110", "1120", "1510", "1520", "3900", "6100", "6500" };

    private static readonly ConcurrentDictionary<string, (DateTime expiresAt, EntityRegistryUnit? unit)> EntityRegistryUnitsCache = new();

    private readonly TimeSpan _cacheEntryTtl = TimeSpan.FromSeconds(1);


    private enum UnitType
    {
        MainUnit,
        SubUnit
    };

    public EntityRegistryService(IEntityRegistryApiClientService entityRegistryApiClientService)
    {
        _entityRegistryApiClientService = entityRegistryApiClientService;
    }

    public async Task<SimpleEntityRegistryUnit?> Get(
        string organizationNumber, 
        bool attemptSubUnitLookupIfNotFound = true,
        bool nestToAndReturnMainUnit = false,
        bool subUnitOnly = false) => MapToEntityRegistryUnit(await GetFull(organizationNumber, attemptSubUnitLookupIfNotFound, nestToAndReturnMainUnit, subUnitOnly));
    

    public async Task<SimpleEntityRegistryUnit?> GetMainUnit(string organizationNumber) => await Get(organizationNumber, attemptSubUnitLookupIfNotFound: false, nestToAndReturnMainUnit: true);

    public async Task<EntityRegistryUnit?> GetFull(string organizationNumber, bool attemptSubUnitLookupIfNotFound = true,
        bool nestToAndReturnMainUnit = false, bool subUnitOnly = false)
    {

        if (IsSyntheticOrganizationNumber(organizationNumber) && !AllowTestCcrLookup)
        {
            return null;
        }

        EntityRegistryUnit? unit;
        if (subUnitOnly)
        {
            unit = await InternalGet(organizationNumber, UnitType.SubUnit);
            if (unit == null) return null;

            if (nestToAndReturnMainUnit && unit.OverordnetEnhet != null)
            {
                var parentUnit = unit;
                do
                {
                    parentUnit = await InternalGet(parentUnit.OverordnetEnhet, UnitType.MainUnit);
                }
                while (parentUnit!.OverordnetEnhet != null);

                return parentUnit;
            }
        }

        unit = await InternalGet(organizationNumber, UnitType.MainUnit);
        if (unit == null && attemptSubUnitLookupIfNotFound)
        {
            return await InternalGet(organizationNumber, UnitType.SubUnit);
        }

        return unit;

    }

    public async Task<EntityRegistryUnit?> GetFullMainUnit(string organizationNumber) => await GetFull(organizationNumber, attemptSubUnitLookupIfNotFound: false, nestToAndReturnMainUnit: true);

    public bool IsMainUnit(SimpleEntityRegistryUnit unit)
    {
        return !IsSubUnit(unit);
    }

    public bool IsMainUnit(EntityRegistryUnit unit) => IsMainUnit(MapToEntityRegistryUnit(unit)!);

    public async Task<bool> IsMainUnit(string organizationNumber)
    {
        var unit = await Get(organizationNumber, attemptSubUnitLookupIfNotFound: false);
        return unit != null && IsMainUnit(unit);
    }

    public bool IsSubUnit(SimpleEntityRegistryUnit unit)
    {
        return !string.IsNullOrEmpty(unit.ParentUnit);
    }

    public bool IsSubUnit(EntityRegistryUnit unit) => IsSubUnit(MapToEntityRegistryUnit(unit)!);
    

    public async Task<bool> IsSubUnit(string organizationNumber)
    {
        var unit = await Get(organizationNumber, subUnitOnly: true);
        return unit != null && IsSubUnit(unit);
    }

    public bool IsPublicAgency(SimpleEntityRegistryUnit unit)
    {
        return PublicSectorUnitTypes.Contains(unit.OrganizationForm)
               || unit.IndustrialCodes.Any(x => x.StartsWith("84"))
               || PublicSectorSectorCodes.Contains(unit.SectorCode);
    }

    public bool IsPublicAgency(EntityRegistryUnit unit) => IsPublicAgency(MapToEntityRegistryUnit(unit)!);

    public async Task<bool> IsPublicAgency(string organizationNumber)
    {
        var unit = await Get(organizationNumber);
        return unit != null && IsPublicAgency(unit);
    }

    private bool IsSyntheticOrganizationNumber(string organizationNumber)
    {
        return organizationNumber.StartsWith("2") || organizationNumber.StartsWith("3");
    }

    private async Task<EntityRegistryUnit?> InternalGet(string organizationNumber, UnitType unitType)
    {
        var cacheKey = Enum.GetName(typeof(UnitType), unitType) + "_" + organizationNumber;
        if (EntityRegistryUnitsCache.TryGetValue(cacheKey, out var cacheEntry) && cacheEntry.expiresAt > DateTime.UtcNow)
        {
            return cacheEntry.unit;
        }

        var urlToFetch = unitType switch
        {
            UnitType.MainUnit => GetLookupUrlForMainUnits(organizationNumber),
            UnitType.SubUnit => GetLookupUrlForSubUnits(organizationNumber),
            _ => throw new InvalidOperationException()
        };

        var entry = (DateTime.UtcNow.Add(_cacheEntryTtl), await GetFromClientService(urlToFetch));
        EntityRegistryUnitsCache.AddOrUpdate(cacheKey, entry, (_, _) => entry);

        return entry.Item2;
    }

    private async Task<EntityRegistryUnit?> GetFromClientService(Uri url)
    {
        return await _entityRegistryApiClientService.GetUpstreamEntityRegistryUnitAsync(url);
    }

    private SimpleEntityRegistryUnit? MapToEntityRegistryUnit(EntityRegistryUnit? upstreamEntityRegistryUnit)
    {
        if (upstreamEntityRegistryUnit == null) return null;

        var unit = new SimpleEntityRegistryUnit
        {
            OrganizationNumber = upstreamEntityRegistryUnit.Organisasjonsnummer.ToString(),
            Name = upstreamEntityRegistryUnit.Navn,
            OrganizationForm = upstreamEntityRegistryUnit.Organisasjonsform.Kode ?? "UNKNOWN",
            ParentUnit = upstreamEntityRegistryUnit.OverordnetEnhet,
            SectorCode = upstreamEntityRegistryUnit.InstitusjonellSektorkode.Kode ?? "UNKNOWN",
            IndustrialCodes = new List<string> { upstreamEntityRegistryUnit.Naeringskode1.Kode ?? "UNKNOWN" },
            IsDeleted = upstreamEntityRegistryUnit.Slettedato != DateTime.MinValue
        };

        if (upstreamEntityRegistryUnit.Naeringskode2 != null) unit.IndustrialCodes.Add(upstreamEntityRegistryUnit.Naeringskode2.Kode ?? "UNKNOWN");
        if (upstreamEntityRegistryUnit.Naeringskode3 != null) unit.IndustrialCodes.Add(upstreamEntityRegistryUnit.Naeringskode3.Kode ?? "UNKNOWN");

        return unit;
    }

    private Uri GetLookupUrlForMainUnits(string organizationNumber)
    {
        string urlPattern;
        if (IsSyntheticOrganizationNumber(organizationNumber))
        {
            urlPattern = UseCoreProxy ? PpeProxyMainUnitLookupEndpoint : PpeMainUnitLookupEndpoint;
        }
        else
        {
            urlPattern = UseCoreProxy ? ProxyMainUnitLookupEndpoint : MainUnitLookupEndpoint;
        }

        return new Uri(string.Format(urlPattern, organizationNumber));
    }

    private Uri GetLookupUrlForSubUnits(string organizationNumber)
    {
        string urlPattern;
        if (IsSyntheticOrganizationNumber(organizationNumber))
        {
            urlPattern = UseCoreProxy ? PpeProxySubUnitLookupEndpoint : PpeSubUnitLookupEndpoint;
        }
        else
        {
            urlPattern = UseCoreProxy ? ProxySubUnitLookupEndpoint : SubUnitLookupEndpoint;
        }

        return new Uri(string.Format(urlPattern, organizationNumber));
    }
}
