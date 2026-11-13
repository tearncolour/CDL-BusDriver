"""Native lamp purposes, matching the built-in exporter's established light order."""


def light_layer(part, finish, height):
    if part == 23:
        return 1 if height > .85 else 2
    if 24 <= part <= 28:
        return {24: 3, 25: 4, 26: 5, 27: 6, 28: 7}[part]
    return 8 if finish == 'Lamp' else 9 if finish == 'Glow' else 0
