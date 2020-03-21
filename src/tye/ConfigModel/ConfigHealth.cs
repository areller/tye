using System.ComponentModel.DataAnnotations;

namespace Microsoft.Tye.ConfigModel
{
    public class ConfigHealth
    {
        [Required] 
        public string Endpoint { get; set; } = default!;
        public int TestInterval { get; set; } = 1;
        public int GracePeriod { get; set; } = 5;
        public int BootPeriod { get; set; } = 5;
    }
}
