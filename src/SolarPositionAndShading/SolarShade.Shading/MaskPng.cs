using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using SolarShade.Core.Models;

namespace SolarShade.Shading;

/// <summary>Fast exact reader for the sibling masker's grayscale8, noninterlaced PNG output.
/// Other encodings return null for OpenCV fallback. CRCs and decoded byte count are checked.</summary>
internal static class MaskPng
{
    private static readonly uint[] CrcTable=Enumerable.Range(0,256).Select(i=>
    {uint c=(uint)i;for(int j=0;j<8;j++)c=(c&1)!=0?0xedb88320^(c>>1):c>>1;return c;}).ToArray();
    public static SkyMask? TryDecode(byte[] png)
    {
        ReadOnlySpan<byte> signature=[137,80,78,71,13,10,26,10];
        if(png.Length<33||!png.AsSpan(0,8).SequenceEqual(signature))return null;
        int width=0,height=0,offset=8;bool header=false,end=false;
        using var compressed=new MemoryStream();
        while(offset<=png.Length-12)
        {
            int length=checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset,4)));
            if(length<0||length>png.Length-offset-12)throw new InvalidDataException("Truncated PNG chunk.");
            var type=png.AsSpan(offset+4,4);var data=png.AsSpan(offset+8,length);
            uint crc=0xffffffff;
            foreach(var b in png.AsSpan(offset+4,length+4))crc=CrcTable[(crc^b)&255]^(crc>>8);
            if((crc^0xffffffff)!=BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset+8+length,4)))throw new InvalidDataException("PNG CRC mismatch.");
            if(type.SequenceEqual("IHDR"u8))
            {
                if(header||offset!=8||length!=13)throw new InvalidDataException("Invalid PNG header.");
                header=true;width=checked((int)BinaryPrimitives.ReadUInt32BigEndian(data));height=checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]));
                if(width<2||height<2||(long)width*height>100000000)throw new InvalidDataException("Unsupported mask dimensions.");
                if(data[8]!=8||data[9]!=0||data[10]!=0||data[11]!=0||data[12]!=0)return null;
            }
            else if(type.SequenceEqual("IDAT"u8))compressed.Write(data);
            else if(type.SequenceEqual("IEND"u8)){end=true;break;}
            offset+=length+12;
        }
        if(!header||!end)throw new InvalidDataException("Incomplete PNG.");
        compressed.Position=0;
        using var z=new ZLibStream(compressed,CompressionMode.Decompress);
        var bytes=new byte[checked(width*height)];var row=new byte[width+1];
        for(int y=0;y<height;y++)
        {
            z.ReadExactly(row);var output=bytes.AsSpan(y*width,width);var input=row.AsSpan(1);var previous=y==0?ReadOnlySpan<byte>.Empty:bytes.AsSpan((y-1)*width,width);
            switch(row[0])
            {
                case 0:input.CopyTo(output);break;
                case 1:
                    byte left=0;for(int x=0;x<width;x++){left=unchecked((byte)(left+input[x]));output[x]=left;}break;
                case 2:
                    if(y==0){input.CopyTo(output);break;}
                    int i=0;
                    if(Vector.IsHardwareAccelerated)for(;i<=width-Vector<byte>.Count;i+=Vector<byte>.Count)
                        (new Vector<byte>(input[i..])+new Vector<byte>(previous[i..])).CopyTo(output[i..]);
                    for(;i<width;i++)output[i]=unchecked((byte)(input[i]+previous[i]));break;
                case 3:
                    for(int x=0;x<width;x++)output[x]=unchecked((byte)(input[x]+((x==0?0:output[x-1])+(y==0?0:previous[x]))/2));break;
                case 4:
                    for(int x=0;x<width;x++)
                    {
                        int a=x==0?0:output[x-1],b=y==0?0:previous[x],c=x==0||y==0?0:previous[x-1];
                        int p=a+b-c,pa=Math.Abs(p-a),pb=Math.Abs(p-b),pc=Math.Abs(p-c);
                        output[x]=unchecked((byte)(input[x]+(pa<=pb&&pa<=pc?a:pb<=pc?b:c)));
                    }
                    break;
                default:throw new InvalidDataException("Unknown PNG filter.");
            }
        }
        if(z.ReadByte()!=-1)throw new InvalidDataException("Unexpected extra PNG scanlines.");
        return new(width,height,bytes);
    }
}
