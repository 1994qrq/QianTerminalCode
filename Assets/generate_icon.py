import os
from PIL import Image, ImageDraw

def create_gradient(width, height, start_color, end_color):
    """Creates a linear gradient image (diagonal)."""
    base = Image.new('RGBA', (width, height), start_color)
    top = Image.new('RGBA', (width, height), end_color)
    mask = Image.new('L', (width, height))
    mask_data = []
    for y in range(height):
        for x in range(width):
            # Diagonal gradient from top-left to bottom-right
            p = (x + y) / (width + height)
            mask_data.append(int(255 * p))
    mask.putdata(mask_data)
    base.paste(top, (0, 0), mask)
    return base

def generate_app_icon():
    # Configuration
    size = 256
    padding = 20
    border_width = 8

    # Colors
    color_cyan = (0, 212, 255)   # #00d4ff
    color_purple = (189, 0, 255) # #bd00ff
    color_bg = (10, 10, 18)      # #0a0a12
    color_white = (255, 255, 255)

    # 1. Create Base Image (Transparent)
    img = Image.new('RGBA', (size, size), (0, 0, 0, 0))

    # 2. Create Mask for Rounded Square
    mask = Image.new('L', (size, size), 0)
    draw_mask = ImageDraw.Draw(mask)
    corner_radius = 60
    draw_mask.rounded_rectangle([(padding, padding), (size - padding, size - padding)],
                                radius=corner_radius, fill=255)

    # 3. Create Gradient Background (Border)
    gradient = create_gradient(size, size, color_cyan, color_purple)

    # 4. Composite Gradient with Shape
    icon_shape = Image.new('RGBA', (size, size), (0,0,0,0))
    icon_shape.paste(gradient, (0,0), mask)

    # 5. Create Inner Dark Background
    inner_padding = padding + border_width
    inner_mask = Image.new('L', (size, size), 0)
    draw_inner = ImageDraw.Draw(inner_mask)
    draw_inner.rounded_rectangle([(inner_padding, inner_padding),
                                  (size - inner_padding, size - inner_padding)],
                                 radius=corner_radius - 5, fill=255)

    # Paste dark background into the center
    dark_layer = Image.new('RGBA', (size, size), color_bg)
    icon_shape.paste(dark_layer, (0,0), inner_mask)

    # 6. Draw "Terminal" Symbol (>_)
    draw = ImageDraw.Draw(icon_shape)

    # Coordinates for Symbol
    center_x, center_y = size // 2, size // 2

    # Draw ">" shape (Cyan Gradient-ish)
    arrow_width = 20
    arrow_size = 60
    arrow_offset_x = -30

    p1 = (center_x + arrow_offset_x - arrow_size//2, center_y - arrow_size)
    p2 = (center_x + arrow_offset_x + arrow_size//2, center_y)
    p3 = (center_x + arrow_offset_x - arrow_size//2, center_y + arrow_size)

    # Glow for >
    for i in range(5, 0, -1):
        alpha = int(50 / i)
        w = arrow_width + (i * 4)
        draw.line([p1, p2, p3], fill=color_cyan + (alpha,), width=w, joint='curve')

    # Main sharp line
    draw.line([p1, p2, p3], fill=color_white, width=arrow_width, joint='curve')

    # Draw "_" shape (Purple cursor)
    cursor_w = 50
    cursor_h = 15
    cursor_x = center_x + 30
    cursor_y = center_y + 40

    cursor_rect = [cursor_x, cursor_y, cursor_x + cursor_w, cursor_y + cursor_h]

    # Glow for _
    for i in range(5, 0, -1):
        alpha = int(50 / i)
        expand = i * 3
        r = [cursor_rect[0]-expand, cursor_rect[1]-expand, cursor_rect[2]+expand, cursor_rect[3]+expand]
        draw.rectangle(r, fill=color_purple + (alpha,))

    draw.rectangle(cursor_rect, fill=color_purple)

    # 7. Save as ICO
    output_dir = r"D:\Qian_code\ai_program\MyAiHelper\Assets"
    if not os.path.exists(output_dir):
        os.makedirs(output_dir)

    output_path = os.path.join(output_dir, "app.ico")

    # Prepare sizes
    sizes = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)]
    icon_images = []

    for s in sizes:
        # High quality downsampling
        resampled = icon_shape.resize(s, Image.Resampling.LANCZOS)
        icon_images.append(resampled)

    # Save as ICO
    icon_images[0].save(output_path, format='ICO', sizes=sizes, append_images=icon_images[1:])
    print(f"Icon generated successfully at: {output_path}")

    # Also save PNG for reference
    png_path = os.path.join(output_dir, "app.png")
    icon_shape.save(png_path, format='PNG')
    print(f"PNG version saved at: {png_path}")

if __name__ == "__main__":
    generate_app_icon()
