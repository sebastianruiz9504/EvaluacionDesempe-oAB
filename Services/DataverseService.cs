using Microsoft.PowerPlatform.Dataverse.Client;

namespace EvaluacionDesempenoAB.Services
{
    public class DataverseService
    {
        private readonly ServiceClient _client;

        public DataverseService(IConfiguration config)
        {
            var conn = config["Dataverse:ConnectionString"];
            _client = new ServiceClient(conn);
        }

        public ServiceClient Client => _client;
    }
}
