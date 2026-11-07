using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace VocabCollector.Desktop.Learning.Effects;

/// <summary>Stationary horizontal ink diffusion; the host animates only its strength.</summary>
public sealed class InkDistortionEffect : ShaderEffect
{
    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty(nameof(Input), typeof(InkDistortionEffect), 0);
    public static readonly DependencyProperty AmountProperty = DependencyProperty.Register(nameof(Amount), typeof(double), typeof(InkDistortionEffect), new UIPropertyMetadata(1d, PixelShaderConstantCallback(0)));
    public static readonly DependencyProperty SurfaceSizeProperty = DependencyProperty.Register(nameof(SurfaceSize), typeof(Point), typeof(InkDistortionEffect), new UIPropertyMetadata(new Point(1, 1), PixelShaderConstantCallback(1)));
    public static readonly DependencyProperty SmearWidthProperty = DependencyProperty.Register(nameof(SmearWidth), typeof(double), typeof(InkDistortionEffect), new UIPropertyMetadata(24d, PixelShaderConstantCallback(2)));
    public static readonly DependencyProperty GrainProperty = DependencyProperty.Register(nameof(Grain), typeof(double), typeof(InkDistortionEffect), new UIPropertyMetadata(.30d, PixelShaderConstantCallback(3)));
    private static readonly PixelShader SharedShader = LoadShader();

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public double Amount { get => (double)GetValue(AmountProperty); set => SetValue(AmountProperty, value); }
    public Point SurfaceSize { get => (Point)GetValue(SurfaceSizeProperty); set => SetValue(SurfaceSizeProperty, value); }
    public double SmearWidth { get => (double)GetValue(SmearWidthProperty); set => SetValue(SmearWidthProperty, value); }
    public double Grain { get => (double)GetValue(GrainProperty); set => SetValue(GrainProperty, value); }

    public InkDistortionEffect()
    {
        PixelShader = SharedShader;
        // The child blur is allowed to bleed outside its layout bounds. Reserve
        // its halo plus refraction instead of clipping the first/last glyph.
        PaddingLeft = PaddingRight = 32;
        PaddingTop = PaddingBottom = 5;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(AmountProperty);
        UpdateShaderValue(SurfaceSizeProperty);
        UpdateShaderValue(SmearWidthProperty);
        UpdateShaderValue(GrainProperty);
    }

    private static PixelShader LoadShader()
    {
        var shader = new PixelShader { UriSource = new Uri("/兔词;component/Learning/Effects/InkDistortion.ps", UriKind.Relative) };
        shader.Freeze();
        return shader;
    }
}
