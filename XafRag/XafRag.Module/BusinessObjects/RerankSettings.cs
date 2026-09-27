using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.Base;

namespace XafRag.Module.BusinessObjects;

/// <summary>
/// RAG-003 spike: re-score retrieved chunks with TypeSafe before they reach the prompt. A single
/// row, seeded by the Updater. Only Administrators have access. The TypeSafe key is stored in
/// this row as entered (like duetGPT's admin-managed provider keys); it is set through the
/// "Set API Key" action and never shown in the UI.
/// </summary>
[DefaultClassOptions]
[NavigationItem("Knowledge Base")]
[DisplayName("Rerank Settings")]
[DefaultProperty(nameof(Caption))]
public class RerankSettings : IXafEntityObject
{
    [Key]
    [VisibleInListView(false), VisibleInDetailView(false), VisibleInLookupListView(false)]
    public virtual int Id { get; set; }

    [NotMapped]
    [Browsable(false)]
    public string Caption => "TypeSafe rerank";

    [DisplayName("Rerank with TypeSafe")]
    public virtual bool Enabled { get; set; }

    [Browsable(false)]
    [FieldSize(FieldSizeAttribute.Unlimited)]
    public virtual string? ApiKey { get; set; }

    [NotMapped]
    [DisplayName("API key configured")]
    public bool ApiKeyConfigured => !string.IsNullOrEmpty(ApiKey);

    public void OnCreated() { }
    public void OnSaving() { }
    public void OnLoaded() { }
}

/// <summary>Popup parameter for "Set API Key": a password field, so the key is typed without being shown.</summary>
[DomainComponent]
[DisplayName("TypeSafe API Key")]
public class ApiKeyInput : NonPersistentBaseObject
{
    [PasswordPropertyText(true)]
    [DisplayName("API key")]
    public string? Key { get; set; }
}
