
% Bookkeeping
clear variables;
close all;

% Parameters
l_step = 1;
lambda = 380:l_step:780;

% Set temperature range
% T_step = 100;
% T = 1000:T_step:10000;
T = linspace(1000, 10000, 128);

% Set redshift range
shift_step = 0.01;
beta = sqrt(1/6);
max_dop = sqrt((1 + beta) / (1 - beta));
max_grav = sqrt(3/2);
max_shift = max_dop * max_grav;
% shift = (1/max_shift):shift_step:max_shift;
shift = linspace(0.5, 2, 128);

% Set up output structure
rgb_out = zeros(length(T), length(shift), 3);

% Loop over temperature and redshift factor
for n = 1:length(T)
    for m = 1:length(shift)
        
        % Calculate XYZ values
        radiance = planck(lambda * shift(m), T(n));
        X = sum(radiance .* x_curve(lambda) * l_step);
        Y = sum(radiance .* y_curve(lambda) * l_step);
        Z = sum(radiance .* z_curve(lambda) * l_step);
        
        % Convert to sRGB
        xyz = [X; Y; Z] / (X + Y + Z);
        RGB = XYZtoRGB(xyz);
        rgb = RGB / max(RGB);
        luminance = [0.2126, 0.7152, 0.0722] * rgb;
        rgb = rgb / luminance;
        rgb_out(n,m,:) = rgb;
    end
end

% Convert to single precision
rgb_out = rgb_out / max(rgb_out, [], 'all');
rgb_out = single(rgb_out);

% Plot result
image(shift, T, rgb_out)
set(gca, 'YDir', 'normal')

% Create TIFF object
filename = 'blackbody.tif';
tiffObject = Tiff(filename, 'w');

% Set TIFF object tags
tagstruct.ImageLength = size(rgb_out,1); 
tagstruct.ImageWidth = size(rgb_out,2);
tagstruct.Compression = Tiff.Compression.None;
tagstruct.SampleFormat = Tiff.SampleFormat.IEEEFP;
tagstruct.Photometric = Tiff.Photometric.RGB;
tagstruct.BitsPerSample = 32;
tagstruct.SamplesPerPixel = size(rgb_out,3);
tagstruct.PlanarConfiguration = Tiff.PlanarConfiguration.Chunky;
tiffObject.setTag(tagstruct);

% Write to file
% NOTE: Flip array vertically, to keep UV coordinates correct.
tiffObject.write(flip(rgb_out, 1));
tiffObject.close;

% Recall image from file
m2 = imread(filename);

% Check that it's the same as what we wrote out.
if any(max(max(m2-flip(rgb_out, 1))) > 0)
    warning('Warning: saved image does not match input data');
end

function rgb = XYZtoRGB(xyz)
    
    M = [ ...
        [ 0.41847,     -0.15866,   -0.082835]; ...
        [-0.091169,     0.25243,    0.015708]; ...
        [ 0.00092090,  -0.0025498,  0.17860]];
    
    rgb = M * xyz;

end

function v = planck(lambda, T)

    % Constants
    c = physconst('LightSpeed');
    k = physconst('Boltzmann');
    h = 6.62607015e-34;

    % Planck's curve at wavelength lambda in nm for temperature T Kelvin
    lambda_m = lambda * 1e-9;
    v = (2 * h * c * c ./ (lambda_m.^5)) ./ (exp(h * c ./ (lambda_m * k * T)) - 1);

end

function v = x_curve(lambda)    

    % Color matching X function for wavelength lambda in nm
    v = 1.056 * g(lambda, 599.8, 37.9, 31.0) + ...
        0.362 * g(lambda, 442.0, 16.0, 26.7) - ...
        0.065 * g(lambda, 501.1, 20.4, 26.2);

end

function v = y_curve(lambda)
    
    % Color matching Y function for wavelength lambda in nm
    v = 0.821 * g(lambda, 568.8, 46.9, 40.5) + ...
        0.286 * g(lambda, 530.9, 16.3, 31.1);

end

function v = z_curve(lambda)
    
    % Color matching Z function for wavelength lambda in nm
    v = 1.217 * g(lambda, 437.0, 11.8, 36.0) + ...
        0.681 * g(lambda, 459.0, 26.0, 13.8);

end

function v_out = g(v_in, mu, sig1, sig2)
    
    if v_in < mu
        v_out = exp(-0.5 * (v_in - mu).^2 / (sig1^2));
    else
        v_out = exp(-0.5 * (v_in - mu).^2 / (sig2^2));
    end
end