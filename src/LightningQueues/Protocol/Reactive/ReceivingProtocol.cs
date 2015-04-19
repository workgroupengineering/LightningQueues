using System;
using System.IO;
using System.Reactive.Linq;
using LightningQueues.Model;

namespace LightningQueues.Protocol.Reactive
{
    public interface IReceivingProtocol
    {
        IObservable<Message> ReceiveStream(IObservable<Stream> streams);
    }

    public class ReactiveReceivingProtocol : IReceivingProtocol
    {
        public virtual IObservable<Message> ReceiveStream(IObservable<Stream> streams)
        {
            return from stream in streams
                   from length in LengthChunk(stream)
                   from messages in MessagesChunk(stream, length).Do(x => {/* Persist here? */ })
                   from message in messages
                   select message;
        }

        public IObservable<int> LengthChunk(Stream stream)
        {
            byte[] buffer = new byte[sizeof(int)];
            return Observable.FromAsync(async () =>
            {
                await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                return BitConverter.ToInt32(buffer, 0);
            });
        }

        public IObservable<Message[]> MessagesChunk(Stream stream, int length)
        {
            byte[] buffer = new byte[length];
            return Observable.FromAsync(async () =>
            {
                try
                {
                    await stream.ReadAsync(buffer, 0, length).ConfigureAwait(false);
                    return SerializationExtensions.ToMessages(buffer);
                }
                catch(Exception)
                {
                    stream.Seek(0, SeekOrigin.End);
                    await stream.WriteAsync(ProtocolConstants.SerializationFailureBuffer,
                                            0, ProtocolConstants.SerializationFailureBuffer.Length).ConfigureAwait(false);
                    throw;
                }
            });
        }
    }
}