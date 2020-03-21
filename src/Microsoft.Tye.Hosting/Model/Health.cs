namespace Microsoft.Tye.Hosting.Model
{
    public class Health
    {
        public string Endpoint { get; set; } = default!;
        public int TestInterval { get; set; } = default!;
        public int GracePeriod { get; set; } = default!;
        public int BootPeriod { get; set; } = default!;
    }
}
