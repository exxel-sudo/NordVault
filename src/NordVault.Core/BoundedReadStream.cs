namespace NordVault.Core;

internal sealed class BoundedReadStream(Stream source,long offset,long length) : Stream
{
    private long _position;
    public override bool CanRead=>true; public override bool CanSeek=>true; public override bool CanWrite=>false;
    public override long Length=>length;
    public override long Position { get=>_position; set { if(value<0||value>length) throw new IOException("Arkivläsningen gick utanför paketet."); _position=value; } }
    public override int Read(byte[] buffer,int start,int count) { source.Position=offset+_position; var read=source.Read(buffer,start,(int)Math.Min(count,length-_position)); _position+=read; return read; }
    public override int Read(Span<byte> buffer) { source.Position=offset+_position; var read=source.Read(buffer[..(int)Math.Min(buffer.Length,length-_position)]); _position+=read; return read; }
    public override long Seek(long position,SeekOrigin origin) { Position=checked((origin==SeekOrigin.Begin?0:origin==SeekOrigin.Current?_position:length)+position); return _position; }
    public override void Flush() { }
    public override void SetLength(long value)=>throw new NotSupportedException();
    public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
}
