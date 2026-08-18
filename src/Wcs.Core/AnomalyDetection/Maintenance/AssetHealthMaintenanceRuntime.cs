namespace Wcs.Core.AnomalyDetection.Maintenance;

public interface IAssetHealthMaintenanceRuntimeStatus
{
    AssetHealthMaintenanceStatus GetStatus();
}
