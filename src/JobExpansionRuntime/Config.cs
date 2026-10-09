using Reloaded.Mod.Interfaces.Structs;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using GenericJobs.Template.Configuration;

namespace GenericJobs.Configuration
{
    public class Config : Configurable<Config>
    {
        [DisplayName("Extra Jobs (Decimal or Hex IDs)")]
        [Description("Comma-separated list of extra job IDs shown across expansion pages (maximum 21).")] 
        [DefaultValue("A0, A1, A2, A3")]
        public string ExtraJobsHex { get; set; } = "A0, A1, A2, A3";
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
