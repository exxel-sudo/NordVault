param()
$ErrorActionPreference='Stop'
$projectPath=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$assetsPath=Join-Path $projectPath 'assets'
$null=New-Item -ItemType Directory -Path $assetsPath -Force
Add-Type -AssemblyName System.Drawing
$bitmap=[Drawing.Bitmap]::new(256,256)
$graphics=[Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([Drawing.Color]::Transparent)
$shape=[Drawing.Drawing2D.GraphicsPath]::new()
$shape.AddArc(4,4,56,56,180,90);$shape.AddArc(196,4,56,56,270,90);$shape.AddArc(196,196,56,56,0,90);$shape.AddArc(4,196,56,56,90,90);$shape.CloseFigure()
$background=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#16283F'))
$outline=[Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#64CBB3'),13)
$inner=[Drawing.Pen]::new([Drawing.Color]::White,13)
try {
    $graphics.FillPath($background,$shape)
    $graphics.DrawPolygon($outline,[Drawing.PointF[]]@([Drawing.PointF]::new(128,47),[Drawing.PointF]::new(209,128),[Drawing.PointF]::new(128,209),[Drawing.PointF]::new(47,128)))
    $graphics.DrawLines($inner,[Drawing.PointF[]]@([Drawing.PointF]::new(91,126),[Drawing.PointF]::new(118,153),[Drawing.PointF]::new(166,102)))
    $memory=[IO.MemoryStream]::new()
    try {
        $bitmap.Save($memory,[Drawing.Imaging.ImageFormat]::Png)
        $png=$memory.ToArray()
        $file=[IO.File]::Create((Join-Path $assetsPath 'NordVault.ico'))
        $writer=[IO.BinaryWriter]::new($file)
        try {
            $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]1)
            $writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([byte]0)
            $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$png.Length);$writer.Write([uint32]22);$writer.Write($png)
        }finally{$writer.Dispose();$file.Dispose()}
    }finally{$memory.Dispose()}
}finally{$inner.Dispose();$outline.Dispose();$background.Dispose();$shape.Dispose();$graphics.Dispose();$bitmap.Dispose()}
