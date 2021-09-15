using System;
using Elastic.Ingest.Apm.Model;
using Elastic.Ingest.Transport;
using Elastic.Transport;

namespace Elastic.Ingest.Apm
{
	public class ApmChannelOptions : TransportChannelOptionsBase<IIntakeObject, EventIntakeResponse, IntakeErrorItem, ApmBufferOptions>
	{
		public ApmChannelOptions(ITransport<ITransportConfiguration> transport) : base(transport) { }
	}


	public class ApmBufferOptions : BufferOptions<IIntakeObject, EventIntakeResponse, IntakeErrorItem>
	{
	}
}