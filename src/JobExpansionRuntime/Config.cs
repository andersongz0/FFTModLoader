using Reloaded.Mod.Interfaces.Structs;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using GenericJobs.Template.Configuration;

namespace GenericJobs.Configuration
{
    public class Config : Configurable<Config>
    {
        [DisplayName("Extra Jobs (Decimal or Hex IDs)")]
        [Description("Optional list of registered job IDs (maximum 21). Empty uses installed mod registrations; unregistered IDs are ignored.")]
        [DefaultValue("")]
        public string ExtraJobsHex { get; set; } = "";
    }

    /// <summary>
    /// Allows you to override certain aspects of the configuration creation process (e.g. create multiple configurations).
    /// Override elements in <see cref="ConfiguratorMixinBase"/> for finer control.
    /// </summary>
    public class ConfiguratorMixin : ConfiguratorMixinBase
    {
        // 
    }
}
