using System.ComponentModel;
using Microsoft.PIX;
using Microsoft.PIX.Extension.DeviceConnection;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

/// <summary>
/// The D3D settings PIX applies to processes it launches (debug layer, DRED, device options).
/// Turning on DRED auto-breadcrumbs/page faults and retaining dump files before launching an app
/// is how a GPU hang produces the .dxdmp_preview file the pix_dump_* tools read.
/// </summary>
[McpServerToolType]
public static class DeviceSettingsTools
{
    [McpServerTool(Name = "pix_device_d3d_settings", ReadOnly = true), Description("Reads the D3D settings PIX applies to processes it launches through this connection: debug layer options (MODE, GBV_MODE, SYNC_COMMAND_QUEUES, ...), DRED options (AUTO_BREADCRUMBS, BREADCRUMB_CONTEXTS, PAGE_FAULTS, WATSON_DUMPS, ...) and device options (FORCE_WARP, FEATURE_LEVEL_LIMIT, DUMP_FILE_DRIVER_OPTIONS, RETAIN_DUMP_FILE, ...), each with its value type. Change one with pix_device_d3d_settings_set before pix_device_launch.")]
    public static Task<string> Get(PixSession session, [Description("Device handle")] string handle, CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_device_d3d_settings", () =>
        {
            ConnectionHandle h = session.Get<ConnectionHandle>(handle);
            IPixD3DSettings settings = PixApiExtensionsDeviceConnection.GetD3DSettings<IPixD3DSettings>(h.Connection);
            return new
            {
                debugLayer = Enum.GetValues<PIX_D3D_SETTING_DEBUG_LAYER>().Select(s => Tools.Try(() => DebugLayer(settings, s), Json.EnumName(s))).ToArray(),
                dred = Enum.GetValues<PIX_D3D_SETTING_DRED>().Select(s => Tools.Try(() => Dred(settings, s), Json.EnumName(s))).ToArray(),
                device = Enum.GetValues<PIX_D3D_SETTING_DEVICE>().Select(s => Tools.Try(() => Device(settings, s), Json.EnumName(s))).ToArray(),
            };
        }, cancellationToken);

    [McpServerTool(Name = "pix_device_d3d_settings_set", Idempotent = true), Description("Changes one D3D setting for processes launched afterwards through this connection (does not affect running processes). category: debugLayer, dred or device. setting: a name from pix_device_d3d_settings, e.g. AUTO_BREADCRUMBS, PAGE_FAULTS, RETAIN_DUMP_FILE, FORCE_WARP, MODE. value: true/false for enable settings; an option name (APP_CONTROLLED, FORCE_ON, FORCE_OFF; for DRED enablement SYSTEM_CONTROLLED, FORCED_ON, FORCED_OFF) for option settings; a number for percentages; a feature level (D3D_FEATURE_LEVEL_12_0) or comma-separated flag names (MEDIUM_OVERHEAD,EVENT_MARKERS) where applicable. To make a GPU hang produce a dump for pix_dump_open: set dred AUTO_BREADCRUMBS and PAGE_FAULTS to FORCED_ON and device RETAIN_DUMP_FILE to true, then pix_device_launch with flags [GPU_CAPTURE_ENABLE_DRED_LOGGING].")]
    public static Task<string> Set(
        PixSession session,
        [Description("Device handle")] string handle,
        [Description("debugLayer, dred or device")] string category,
        [Description("Setting name, e.g. AUTO_BREADCRUMBS (see pix_device_d3d_settings).")] string setting,
        [Description("New value: true/false, an option or enum name, a number, or comma-separated flag names depending on the setting type.")] string value,
        CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_device_d3d_settings_set", () =>
        {
            ConnectionHandle h = session.Get<ConnectionHandle>(handle);
            IPixD3DSettings settings = PixApiExtensionsDeviceConnection.GetD3DSettings<IPixD3DSettings>(h.Connection);
            object result;
            switch (category.Trim().ToLowerInvariant())
            {
                case "debuglayer":
                case "debug_layer":
                {
                    var s = Tools.ParseEnum<PIX_D3D_SETTING_DEBUG_LAYER>(setting);
                    PIX_D3D_SETTING_DEBUG_LAYER_DESC desc = default;
                    _IPixD3DSettings_Extensions.GetDebugLayerSetting(settings, s, ref desc);
                    switch (desc.Type)
                    {
                        case PIX_D3D_SETTING_DEBUG_LAYER_TYPE.PIX_D3D_SETTING_DEBUG_LAYER_TYPE_ENABLE: desc.Anonymous.Enable = ParseBool(value); break;
                        case PIX_D3D_SETTING_DEBUG_LAYER_TYPE.PIX_D3D_SETTING_DEBUG_LAYER_TYPE_APP_OPTION: desc.Anonymous.AppOption = Tools.ParseEnum<PIX_D3D_SETTING_APP_OPTION>(value); break;
                        case PIX_D3D_SETTING_DEBUG_LAYER_TYPE.PIX_D3D_SETTING_DEBUG_LAYER_TYPE_PERCENTAGE_FACTOR: desc.Anonymous.PercentageFactor = ParseUInt(value); break;
                        case PIX_D3D_SETTING_DEBUG_LAYER_TYPE.PIX_D3D_SETTING_DEBUG_LAYER_TYPE_SHADER_PATCH_MODE: desc.Anonymous.ShaderPatchMode = Tools.ParseEnum<D3D12_GPU_BASED_VALIDATION_SHADER_PATCH_MODE>(value); break;
                        default: throw new McpException($"Setting {Json.EnumName(s)} has an unknown value type {desc.Type}.");
                    }
                    _IPixD3DSettings_Extensions.SetDebugLayerSetting(settings, ref desc);
                    result = DebugLayer(settings, s);
                    break;
                }
                case "dred":
                {
                    var s = Tools.ParseEnum<PIX_D3D_SETTING_DRED>(setting);
                    PIX_D3D_SETTING_DRED_DESC desc = default;
                    _IPixD3DSettings_Extensions.GetDredSetting(settings, s, ref desc);
                    switch (desc.Type)
                    {
                        case PIX_D3D_SETTING_DRED_TYPE.PIX_D3D_SETTING_DRED_TYPE_DRED_ENABLEMENT: desc.Anonymous.DredEnablement = Tools.ParseEnum<D3D12_DRED_ENABLEMENT>(value); break;
                        case PIX_D3D_SETTING_DRED_TYPE.PIX_D3D_SETTING_DRED_TYPE_APP_OPTION: desc.Anonymous.AppOption = Tools.ParseEnum<PIX_D3D_SETTING_APP_OPTION>(value); break;
                        default: throw new McpException($"Setting {Json.EnumName(s)} has an unknown value type {desc.Type}.");
                    }
                    _IPixD3DSettings_Extensions.SetDredSetting(settings, ref desc);
                    result = Dred(settings, s);
                    break;
                }
                case "device":
                {
                    var s = Tools.ParseEnum<PIX_D3D_SETTING_DEVICE>(setting);
                    PIX_D3D_SETTING_DEVICE_DESC desc = default;
                    _IPixD3DSettings_Extensions.GetDeviceSetting(settings, s, ref desc);
                    switch (desc.Type)
                    {
                        case PIX_D3D_SETTING_DEVICE_TYPE.PIX_D3D_SETTING_DEVICE_TYPE_ENABLE: desc.Anonymous.Enable = ParseBool(value); break;
                        case PIX_D3D_SETTING_DEVICE_TYPE.PIX_D3D_SETTING_DEVICE_TYPE_APP_OPTION: desc.Anonymous.AppOption = Tools.ParseEnum<PIX_D3D_SETTING_APP_OPTION>(value); break;
                        case PIX_D3D_SETTING_DEVICE_TYPE.PIX_D3D_SETTING_DEVICE_TYPE_D3D_FEATURE_LEVEL: desc.Anonymous.FeatureLevel = Tools.ParseEnum<D3D_FEATURE_LEVEL>(value); break;
                        case PIX_D3D_SETTING_DEVICE_TYPE.PIX_D3D_SETTING_DEVICE_TYPE_DUMP_FILE_DRIVER_OPTIONS: desc.Anonymous.DumpFileDriverOptions = ParseFlags<D3D12_DUMP_FILE_DRIVER_OPTIONS>(value); break;
                        default: throw new McpException($"Setting {Json.EnumName(s)} has an unknown value type {desc.Type}.");
                    }
                    _IPixD3DSettings_Extensions.SetDeviceSetting(settings, ref desc);
                    result = Device(settings, s);
                    break;
                }
                default:
                    throw new McpException($"Unknown category '{category}'. Use debugLayer, dred or device.");
            }
            h.Note("d3dSettingChanged", new { category, setting, value });
            return new { changed = true, category, result };
        }, cancellationToken);

    private static object DebugLayer(IPixD3DSettings settings, PIX_D3D_SETTING_DEBUG_LAYER setting)
    {
        PIX_D3D_SETTING_DEBUG_LAYER_DESC desc = default;
        _IPixD3DSettings_Extensions.GetDebugLayerSetting(settings, setting, ref desc);
        object? value = desc.Type switch
        {
            PIX_D3D_SETTING_DEBUG_LAYER_TYPE.PIX_D3D_SETTING_DEBUG_LAYER_TYPE_ENABLE => (bool)desc.Anonymous.Enable,
            PIX_D3D_SETTING_DEBUG_LAYER_TYPE.PIX_D3D_SETTING_DEBUG_LAYER_TYPE_APP_OPTION => desc.Anonymous.AppOption,
            PIX_D3D_SETTING_DEBUG_LAYER_TYPE.PIX_D3D_SETTING_DEBUG_LAYER_TYPE_PERCENTAGE_FACTOR => desc.Anonymous.PercentageFactor,
            PIX_D3D_SETTING_DEBUG_LAYER_TYPE.PIX_D3D_SETTING_DEBUG_LAYER_TYPE_SHADER_PATCH_MODE => desc.Anonymous.ShaderPatchMode,
            _ => null,
        };
        return new { setting, type = desc.Type, apiConstraint = desc.ApiConstraint, value };
    }

    private static object Dred(IPixD3DSettings settings, PIX_D3D_SETTING_DRED setting)
    {
        PIX_D3D_SETTING_DRED_DESC desc = default;
        _IPixD3DSettings_Extensions.GetDredSetting(settings, setting, ref desc);
        object? value = desc.Type switch
        {
            PIX_D3D_SETTING_DRED_TYPE.PIX_D3D_SETTING_DRED_TYPE_DRED_ENABLEMENT => desc.Anonymous.DredEnablement,
            PIX_D3D_SETTING_DRED_TYPE.PIX_D3D_SETTING_DRED_TYPE_APP_OPTION => desc.Anonymous.AppOption,
            _ => null,
        };
        return new { setting, type = desc.Type, apiConstraint = desc.ApiConstraint, value };
    }

    private static object Device(IPixD3DSettings settings, PIX_D3D_SETTING_DEVICE setting)
    {
        PIX_D3D_SETTING_DEVICE_DESC desc = default;
        _IPixD3DSettings_Extensions.GetDeviceSetting(settings, setting, ref desc);
        object? value = desc.Type switch
        {
            PIX_D3D_SETTING_DEVICE_TYPE.PIX_D3D_SETTING_DEVICE_TYPE_ENABLE => (bool)desc.Anonymous.Enable,
            PIX_D3D_SETTING_DEVICE_TYPE.PIX_D3D_SETTING_DEVICE_TYPE_APP_OPTION => desc.Anonymous.AppOption,
            PIX_D3D_SETTING_DEVICE_TYPE.PIX_D3D_SETTING_DEVICE_TYPE_D3D_FEATURE_LEVEL => desc.Anonymous.FeatureLevel,
            PIX_D3D_SETTING_DEVICE_TYPE.PIX_D3D_SETTING_DEVICE_TYPE_DUMP_FILE_DRIVER_OPTIONS => desc.Anonymous.DumpFileDriverOptions,
            _ => null,
        };
        return new { setting, type = desc.Type, apiConstraint = desc.ApiConstraint, value };
    }

    internal static bool ParseBool(string value) => value.Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "on" or "yes" or "enable" or "enabled" => true,
        "false" or "0" or "off" or "no" or "disable" or "disabled" => false,
        _ => throw new McpException($"Expected true or false, got '{value}'."),
    };

    internal static uint ParseUInt(string value)
        => uint.TryParse(value.Trim(), out uint parsed) ? parsed : throw new McpException($"Expected a non-negative integer, got '{value}'.");

    internal static T ParseFlags<T>(string value) where T : struct, Enum
    {
        long combined = 0;
        foreach (string part in value.Split(new[] { ',', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            combined |= Convert.ToInt64(Tools.ParseEnum<T>(part));
        }
        return (T)Enum.ToObject(typeof(T), combined);
    }
}
