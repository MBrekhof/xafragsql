using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Actions;
using DevExpress.ExpressApp.SystemModule;
using DevExpress.Persistent.Base;
using XafRag.Module.BusinessObjects;

namespace XafRag.Blazor.Server.Controllers;

/// <summary>
/// Sets or clears the TypeSafe API key on <see cref="RerankSettings"/>. The key is typed into a
/// password field in a popup, so it is never displayed; the entity's own ApiKey member is hidden.
/// The actions only change the value; the user saves as with any edit.
/// </summary>
public class RerankSettingsController : ObjectViewController<DetailView, RerankSettings>
{
    public RerankSettingsController()
    {
        var setKey = new PopupWindowShowAction(this, "SetTypeSafeApiKey", PredefinedCategory.Edit)
        {
            Caption = "Set API Key",
            ImageName = "Security_Key",
        };
        setKey.CustomizePopupWindowParams += SetKey_CustomizePopupWindowParams;
        setKey.Execute += SetKey_Execute;

        var clearKey = new SimpleAction(this, "ClearTypeSafeApiKey", PredefinedCategory.Edit)
        {
            Caption = "Clear API Key",
        };
        clearKey.Execute += (_, _) => ViewCurrentObject.ApiKey = null;
    }

    private void SetKey_CustomizePopupWindowParams(object? sender, CustomizePopupWindowParamsEventArgs e)
    {
        var os = Application.CreateObjectSpace(typeof(ApiKeyInput));
        e.View = Application.CreateDetailView(os, os.CreateObject<ApiKeyInput>(), true);
        e.DialogController.SaveOnAccept = false;
    }

    private void SetKey_Execute(object? sender, PopupWindowShowActionExecuteEventArgs e)
    {
        var input = (ApiKeyInput)e.PopupWindowViewCurrentObject;
        var key = input.Key?.Trim();
        input.Key = null;
        if (!string.IsNullOrEmpty(key))
            ViewCurrentObject.ApiKey = key;
    }
}

/// <summary>
/// RerankSettings is a single row (seeded by the Updater; RagService reads the first one), so hide
/// the actions that would create or delete rows. Administrators bypass permissions, so this can't
/// be done with a role.
/// </summary>
public class RerankSettingsSingletonController : ObjectViewController<ObjectView, RerankSettings>
{
    private const string Key = "RerankSettingsSingleton";

    protected override void OnActivated()
    {
        base.OnActivated();
        SetActive(false);
    }

    protected override void OnDeactivated()
    {
        SetActive(true);
        base.OnDeactivated();
    }

    private void SetActive(bool active)
    {
        if (Frame.GetController<NewObjectViewController>() is { } newController)
            Toggle(newController.NewObjectAction, active);
        if (Frame.GetController<DeleteObjectsViewController>() is { } deleteController)
            Toggle(deleteController.DeleteAction, active);
        if (Frame.GetController<ModificationsController>() is { } modifications)
            Toggle(modifications.SaveAndNewAction, active);
    }

    private static void Toggle(ActionBase action, bool active)
    {
        if (active) action.Active.RemoveItem(Key);
        else action.Active[Key] = false;
    }
}
