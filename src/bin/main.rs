extern crate glsl2hlsl;
use glsl2hlsl::*;
use std::path::PathBuf;

fn print_usage() {
    println!("Usage:");
    println!("  glsl2hlsl <filename>");
    println!("  glsl2hlsl --shadertoy <url-or-id> [output-dir]");
}

fn extract_shadertoy_id(input: &str) -> Option<String> {
    let trimmed = input.trim();
    if trimmed.is_empty() {
        return None;
    }

    if let Some(pos) = trimmed.find("/view/") {
        let start = pos + "/view/".len();
        let id = trimmed[start..]
            .split(|c| c == '/' || c == '?' || c == '#')
            .next()
            .unwrap_or_default();
        if !id.is_empty() {
            return Some(id.to_string());
        }
    }

    Some(
        trimmed
            .split(|c| c == '/' || c == '?' || c == '#')
            .next()
            .unwrap_or(trimmed)
            .to_string(),
    )
}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    if args.len() < 2 {
        print_usage();
        return;
    }

    if args[1] == "--shadertoy" || args[1] == "-s" {
        if args.len() < 3 {
            print_usage();
            return;
        }

        let id = extract_shadertoy_id(&args[2]).expect("Invalid ShaderToy URL or ID");
        let shader = download_shader(&id).expect("Error downloading ShaderToy shader");
        let files = get_files(&shader, true, true);
        let output_dir = if args.len() >= 4 {
            PathBuf::from(&args[3])
        } else {
            std::env::current_dir().expect("Error reading current directory")
        };

        std::fs::create_dir_all(&output_dir).expect("Error creating output directory");
        for file in files {
            let path = output_dir.join(file.name);
            std::fs::write(&path, file.contents).expect("Error writing ShaderToy output file");
        }
        return;
    }

    let path = std::path::Path::new(args[1].as_str());
    let glsl = std::fs::read_to_string(path).expect("Error reading file");

    let compiled = transpile(glsl, true, true);

    let mut arg = args[1].clone();
    arg.push_str(".shader");
    let path = std::path::Path::new(arg.as_str());
    std::fs::write(path, compiled).expect("Error writing file");
}
