namespace NordVault.Core;
internal sealed class OffsetWriteStream(Stream target,long offset) : Stream
{
    public override bool CanRead=>false; public override bool CanSeek=>target.CanSeek; public override bool CanWrite=>true;
    public override long Length=>target.Length-offset;
    public override long Position { get=>target.Position-offset; set { if(value<0) throw new IOException("Ogiltig arkivposition."); target.Position=checked(offset+value); } }
    public override void Flush()=>target.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken)=>target.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    public override long Seek(long value,SeekOrigin origin) { Position=checked((origin==SeekOrigin.Begin?0:origin==SeekOrigin.Current?Position:Length)+value); return Position; }
    public override void SetLength(long value) { if(value<0) throw new IOException(); target.SetLength(checked(offset+value)); }
    public override void Write(byte[] buffer,int start,int count)=>target.Write(buffer,start,count);
    public override void Write(ReadOnlySpan<byte> buffer)=>target.Write(buffer);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellationToken=default)=>target.WriteAsync(buffer,cancellationToken);
}
